﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Stac;
using Terradue.Stars.Data.Model.Atom;
using Terradue.OpenSearch.Result;
using Terradue.ServiceModel.Syndication;
using Terradue.Stars.Common;
using Terradue.Stars.Interface;
using Terradue.Stars.Interface.Router;
using Terradue.Stars.Services.Model.Atom;
using Terradue.Stars.Services.Model.Stac;
using Terradue.Stars.Services.Router;
using Terradue.Stars.Services.Store;
using Terradue.Stars.Services.ThirdParty.Titiler;
using Terradue.Stars.Services.Translator;
using Terradue.Stars.Services;
using System.Threading;
using Terradue.Stars.Services.Model;
using System.Collections.Specialized;
using System.Web;

namespace Terradue.Stars.Data.ThirdParty.Geosquare
{
    public class GeosquareService : ICatalogService
    {
        private readonly RouterService routingService;
        private readonly TranslatorManager translatorManager;
        private readonly GeosquareConfiguration geosquareConfiguration;
        private readonly TitilerService titilerService;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly ICredentials credentials;
        private readonly IResourceServiceProvider resourceServiceProvider;
        private readonly ILogger<GeosquareService> logger;

        public GeosquareConfiguration GeosquareConfiguration => geosquareConfiguration;

        public GeosquareService(RouterService routerService,
                                  TranslatorManager translatorManager,
                                  IOptions<GeosquareConfiguration> geosquareConfiguration,
                                  TitilerService titilerService,
                                  IHttpClientFactory httpClientFactory,
                                  ICredentials credentials,
                                  IResourceServiceProvider resourceServiceProvider,
                                  ILogger<GeosquareService> logger)
        {
            this.routingService = routerService;
            this.translatorManager = translatorManager;
            this.geosquareConfiguration = geosquareConfiguration.Value;
            this.titilerService = titilerService;
            this.httpClientFactory = httpClientFactory;
            this.credentials = credentials;
            this.resourceServiceProvider = resourceServiceProvider;
            this.logger = logger;
        }

        public async Task<IPublicationState> PublishAsync(IPublicationModel publicationModel, CancellationToken ct)
        {
            // Get the client to use with the catalog Id
            HttpClient client = CreateClient(publicationModel.CatalogId);
            GeosquarePublicationModel geosquareModel = publicationModel as GeosquarePublicationModel;
            if (geosquareModel == null)
            {
                geosquareModel = CreateModelFromPublication(publicationModel);
            }
            if (geosquareModel.CreateIndex) await CreateIndexIfNotExist(geosquareModel, client);
            InitRoutingTask(geosquareModel);
            var guid = CalculateHash(geosquareModel.Url.ToString());
            var route = await resourceServiceProvider.GetStreamResourceAsync(new GenericResource(new Uri(geosquareModel.Url)), ct);

            GeosquarePublicationState state = new GeosquarePublicationState(geosquareModel, client);
            state.Hash = guid;
            await routingService.RouteAsync(route, 4, null, state, ct);

            state.OsdUri = new Uri(client.BaseAddress,
                            string.Format("{0}/cat/{1}/description", geosquareModel.Index, guid.Value));

            return state;
        }

        private HttpClient CreateClient(string catalogId)
        {
            HttpClient client = httpClientFactory.CreateClient(catalogId);
            if (client.BaseAddress == null)
            {
                client.BaseAddress = new Uri(catalogId);
            }
            return client;
        }

        private GeosquarePublicationModel CreateModelFromPublication(IPublicationModel publicationModel)
        {
            return new GeosquarePublicationModel
            {
                Url = publicationModel.Url,
                Index = geosquareConfiguration.DefaultIndex,
                AdditionalLinks = publicationModel.AdditionalLinks,
                CreateIndex = true,
                SubjectsList = publicationModel.Subjects?.Select(s => new Subject(s)).ToList(),
                CatalogId = publicationModel.CatalogId ?? geosquareConfiguration.BaseUri.ToString()
            };
        }

        private KeyValuePair<string, string> CalculateHash(string input)
        {
            using (var algorithm = SHA256.Create()) //or MD5 SHA256 etc.
            {
                var hashedBytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
                string hash = BitConverter.ToString(hashedBytes).ToLower();
                return new KeyValuePair<string, string>(string.Join("", hash.Split('-').Take(4)), hash.Replace("-", ""));
            }
        }

        private void InitRoutingTask(GeosquarePublicationModel geosquareModel)
        {
            routingService.Parameters = new RouterServiceParameters()
            {
                Recursivity = geosquareModel.Depth,
                SkipAssets = true
            };
            // routingService.OnRoutingException((route, router, exception, state) => PrintRouteInfo(route, router, exception, state));
            routingService.OnBeforeBranching((node, router, state, subroutes, ct) => OnBeforeBranching(node, router, state, subroutes, ct));
            routingService.OnItem((node, router, state, ct) => PostItemToCatalog(node, router, state, ct));
            // routingService.OnBranching((parentRoute, route, siblings, state) => PrepareNewRoute(parentRoute, route, siblings, state));
        }

        private async Task<object> OnBeforeBranching(ICatalog node, IRouter router, object state, ICollection<IResource> subroutes, CancellationToken ct)
        {
            var collection = (node as StacCatalogNode).StacCatalog as StacCollection;
            if (collection == null)
            {
                return state;
            }

            // If Collection has assets, we consider it as a single item
            if (collection.Assets.Count > 0)
            {
                await PostCollectionToCatalog(new StacCollectionNode(collection, node.Uri), router, state, ct);
            }

            return state;
        }

        public async Task<object> PostCollectionToCatalog(Terradue.Stars.Interface.ICollection collectionNode, IRouter router, object state, CancellationToken ct)
        {
            GeosquarePublicationState catalogPublicationState = state as GeosquarePublicationState;
            AtomItemNode atomItemNode = null;
            try
            {
                atomItemNode = await translatorManager.TranslateAsync<AtomItemNode>(collectionNode, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Unable to translate item {0} to AtomItemNode", collectionNode);
                if (catalogPublicationState.GeosquarePublicationModel.ThrowPublicationException)
                    throw;

                atomItemNode = null;
            }
            if (atomItemNode == null) return state;

            atomItemNode.AtomItem.Identifier = catalogPublicationState.Hash.Key + "-" + atomItemNode.Identifier;
            atomItemNode.AtomItem.Categories.Add(new SyndicationCategory(catalogPublicationState.Hash.Value, "http://www.terradue.com/opensearch/hash", catalogPublicationState.Hash.Value));

            await PrepareAtomItem(atomItemNode.AtomItem, catalogPublicationState, collectionNode);

            await PublishAtomFeed(atomItemNode.AtomItem.ToAtomFeed(), catalogPublicationState.GeosquarePublicationModel, catalogPublicationState.Client);

            return state;
        }

        public async Task<object> PostItemToCatalog(IItem itemNode, IRouter router, object state, CancellationToken ct)
        {
            GeosquarePublicationState catalogPublicationState = state as GeosquarePublicationState;
            AtomItemNode atomItemNode = null;
            try
            {
                atomItemNode = await translatorManager.TranslateAsync<AtomItemNode>(itemNode, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Unable to translate item {0} to AtomItemNode", itemNode);
                if (catalogPublicationState.GeosquarePublicationModel.ThrowPublicationException)
                    throw;

                atomItemNode = null;
            }
            if (atomItemNode == null) return state;

            atomItemNode.AtomItem.Identifier = catalogPublicationState.Hash.Key + "-" + atomItemNode.Identifier;
            atomItemNode.AtomItem.Categories.Add(new SyndicationCategory(catalogPublicationState.Hash.Value, "http://www.terradue.com/opensearch/hash", catalogPublicationState.Hash.Value));

            await PrepareAtomItem(atomItemNode.AtomItem, catalogPublicationState, itemNode);

            await PublishAtomFeed(atomItemNode.AtomItem.ToAtomFeed(), catalogPublicationState.GeosquarePublicationModel, catalogPublicationState.Client);

            return state;
        }

        public async Task PrepareAtomItem(AtomItem atomItem, GeosquarePublicationState geosquarePublicationState, IAssetsContainer assetsContainer)
        {
            // remap all link
            foreach (var link in atomItem.Links)
            {
                link.Uri = geosquareConfiguration.MapUri(link.Uri);
                geosquarePublicationState.GeosquarePublicationModel.UpdateLink(link, atomItem, assetsContainer);
            }

            // create eventual opensearch link
            if (atomItem is StarsAtomItem)
            {
                await (atomItem as StarsAtomItem).CreateOpenSearchLinks(this.CreateOpenSearchLinkAsync, geosquarePublicationState);
            }

            //add links
            if (geosquarePublicationState.GeosquarePublicationModel != null && geosquarePublicationState.GeosquarePublicationModel.AdditionalLinks != null)
            {
                foreach (var link in geosquarePublicationState.GeosquarePublicationModel.AdditionalLinks)
                    atomItem.Links.Add(link.ToSyndicationLink());
            }

            //add categories
            if (geosquarePublicationState.GeosquarePublicationModel != null && geosquarePublicationState.GeosquarePublicationModel.SubjectsList != null)
            {
                foreach (var subject in geosquarePublicationState.GeosquarePublicationModel.SubjectsList)
                    atomItem.Categories.Add(subject.ToSyndicationCategory());
            }
        }

        public async Task CreateIndexIfNotExist(GeosquarePublicationModel pubModel, HttpClient client)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress, pubModel.Index + "/_exists"));
            var wr = await client.SendAsync(request);
            if (wr.StatusCode == HttpStatusCode.OK) return;

            if (wr.StatusCode == HttpStatusCode.Unauthorized || wr.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new Exception($"Unable to access index {pubModel.Index} : {wr.StatusCode}");
            }

            logger.LogDebug("Creating index {0}", pubModel.Index);
            var webresponse = await client.PutAsync(pubModel.Index, null);
            string response = await webresponse.Content.ReadAsStringAsync();
            logger.LogDebug(response);
        }

        public async Task<string> PublishAtomFeed(AtomFeed atomFeed, GeosquarePublicationModel pubModel, HttpClient httpClient)
        {
            var content = GetAtomContent(atomFeed);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/atom+xml");
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, pubModel.Index);
            request.Content = content;
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            var webresponse = await httpClient.SendAsync(request);
            string response = await webresponse.Content.ReadAsStringAsync();
            logger.LogDebug(response);
            return response;
        }

        public StacLink CreateOpenSearchDescriptionStacLinkFromCategoryName(Uri baseUri, string catName, string index)
        {
            return new StacLink(new Uri(baseUri,
                                        string.Format("{0}/cat/{1}/description", index, catName)),
                "search", "Opensearch description", "application/opensearchdescription+xml");
        }

        protected virtual StringContent GetAtomContent(AtomFeed atomFeed)
        {
            var stringContent = atomFeed.SerializeToString().Replace("utf-8", "utf-32");
            return new StringContent(stringContent, Encoding.UTF32);
        }

        public async Task<SyndicationLink> CreateOpenSearchLinkAsync(SyndicationLink link, object state)
        {
            GeosquarePublicationState catalogPublicationState = state as GeosquarePublicationState;
            try
            {
                var template = geosquareConfiguration.GetOpenSearchForUri(link.Uri);
                if (string.IsNullOrEmpty(template)) return null;
                var webRoute = await resourceServiceProvider.GetStreamResourceAsync(new AtomResourceLink(link), System.Threading.CancellationToken.None);
                IStacObject linkedStacObject = StacConvert.Deserialize<IStacObject>(await webRoute.GetStreamAsync(System.Threading.CancellationToken.None));
                var osUrl = template.ReplaceMacro<IStacObject>("stacObject", linkedStacObject);
                osUrl = osUrl.ReplaceMacro<string>("index", catalogPublicationState.GeosquarePublicationModel.Index);
                var osUri = new Uri(osUrl);

                var relatedLink = new SyndicationLink(
                    osUri,
                    "results",
                    "Search for " + link.Title,
                    "application/atom+xml", 0
                );

            }
            catch { }
            return null;
        }

        public async Task DeleteQueryAsync(HttpClient httpClient, string index, NameValueCollection osParameters)
        {
            //Init
            var uriBuilder = new UriBuilder(new Uri(httpClient.BaseAddress, index + "/query"));
            var queryString = HttpUtility.ParseQueryString(uriBuilder.Query);

            //Extend
            queryString.Add(osParameters);

            //Overwrite original
            uriBuilder.Query = queryString.ToString();
            var deleteUri = uriBuilder.ToString();

            logger.LogDebug("Removing items in index {0} ({1})", index, deleteUri.ToString());

            var webresponse = await httpClient.DeleteAsync(deleteUri);
            webresponse.EnsureSuccessStatusCode();
            string response = await webresponse.Content.ReadAsStringAsync();
            logger.LogDebug(response);

        }
    }
}