using System;
using System.Threading.Tasks;
using System.Xml;
using Terradue.Stars.Interface.Router;
using Terradue.ServiceModel.Syndication;
using System.Net;
using Terradue.Stars.Interface;
using Terradue.Stars.Services.Plugins;
using Terradue.OpenSearch.Result;
using Terradue.Stars.Services.Router;
using System.Linq;
using Terradue.Stars.Services.Resources;
using Terradue.Stars.Services.Supplier;
using System.IO;
using System.Threading;

namespace Terradue.Stars.Services.Model.Atom
{
    [PluginPriority(10)]
    public class AtomRouter : IRouter
    {

        private static string[] supportedTypes = new string[] { "application/atom+xml", "application/xml", "text/xml" };

        private readonly IResourceServiceProvider resourceServiceProvider;

        public AtomRouter(IResourceServiceProvider resourceServiceProvider)
        {
            this.resourceServiceProvider = resourceServiceProvider;
        }

        public int Priority { get; set; }
        public string Key { get => "Atom"; set { } }

        public string Label => "Atom Native Router";

        public bool CanRoute(IResource node)
        {
            var affinedRoute = AffineRouteAsync(node, CancellationToken.None).Result;
            if (!supportedTypes.Contains(affinedRoute.ContentType.MediaType)) return false;
            try
            {
                Atom10FeedFormatter feedFormatter = new Atom10FeedFormatter();
                feedFormatter.ReadFrom(XmlReader.Create(FetchResourceAsync(affinedRoute, CancellationToken.None).Result.GetStreamAsync(CancellationToken.None).Result));
                return true;
            }
            catch { }
            try
            {
                Atom10ItemFormatter itemFormatter = new Atom10ItemFormatter();
                itemFormatter.ReadFrom(XmlReader.Create(FetchResourceAsync(affinedRoute, CancellationToken.None).Result.GetStreamAsync(CancellationToken.None).Result));
                return true;
            }
            catch { }

            return false;
        }

        private async Task<IResource> AffineRouteAsync(IResource route, CancellationToken ct)
        {
            if (supportedTypes.Contains(route.ContentType.MediaType)
                && route is IStreamResource)
            {
                return route;
            }
            IResource newRoute = await resourceServiceProvider.CreateStreamResourceAsync(new GenericResource(new Uri(route.Uri.ToString())), ct);
            return newRoute;
        }

        public async Task<IResource> RouteAsync(IResource node, CancellationToken ct)
        {
            var affinedRoute = AffineRouteAsync(node, ct).Result;
            try
            {
                Atom10FeedFormatter feedFormatter = new Atom10FeedFormatter();
                await Task.Run(() => feedFormatter.ReadFrom(XmlReader.Create(FetchResourceAsync(affinedRoute, ct).Result.GetStreamAsync(ct).Result)));
                return new AtomFeedCatalog(new AtomFeed(feedFormatter.Feed), affinedRoute.Uri);
            }
            catch (Exception)
            {
                try
                {
                    Atom10ItemFormatter itemFormatter = new Atom10ItemFormatter();
                    await Task.Run(() => itemFormatter.ReadFrom(XmlReader.Create(FetchResourceAsync(affinedRoute, ct).Result.GetStreamAsync(ct).Result)));
                    return new AtomItemNode(new AtomItem(itemFormatter.Item), affinedRoute.Uri);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public async Task<IStreamResource> FetchResourceAsync(IResource node, CancellationToken ct)
        {
            if (node is HttpResource && node.Uri.Query.Contains("format=json"))
            {
                return await resourceServiceProvider.CreateStreamResourceAsync(new GenericResource(new Uri(node.Uri.ToString().Replace("format=json", "format=atom"))), ct);
            }

            if (node is IStreamResource) return node as IStreamResource;

            return await resourceServiceProvider.GetStreamResourceAsync(node, ct);
        }

        public async Task<IResource> RouteLinkAsync(IResource resource, IResourceLink childLink, CancellationToken ct)
        {
            if (!(resource is AtomFeedCatalog) 
                && !(resource is AtomItemNode))
            {
                throw new Exception("Cannot route link from non-atom resource");
            }
            var link = resourceServiceProvider.ComposeLinkUri(childLink, resource);
            return await RouteAsync(new GenericResource(link), ct);
        }

    }
}
