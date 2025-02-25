using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Stac;
using Terradue.Stars.Interface.Router;
using Terradue.Stars.Interface.Supplier;
using Terradue.Stars.Services.Router;
using Terradue.ServiceModel.Syndication;
using Terradue.Stars.Services;
using GeoJSON.Net.Geometry;
using Terradue.Stars.Geometry.Atom;
using System.Net;
using Terradue.Stars.Interface;
using Terradue.OpenSearch.Result;
using Itenso.TimePeriod;
using System.Threading;

namespace Terradue.Stars.Services.Model.Atom
{
    public class AtomItemNode : IItem, IAssetsContainer, IStreamResource
    {
        private AtomItem item;
        private readonly Uri sourceUri;

        public AtomItemNode(AtomItem item, Uri sourceUri)
        {
            this.item = item;
            this.sourceUri = sourceUri;
        }

        public AtomItem AtomItem => item;

        public string Title => item.Title != null ? item.Title.Text : item.Id;

        public ContentType ContentType => new ContentType("application/atom+xml");

        public Uri Uri => sourceUri ?? item.Links.FirstOrDefault(link => link.RelationshipType == "self").Uri;

        public ResourceType ResourceType => ResourceType.Item;

        public string Id => Identifier ?? item.Id.CleanIdentifier();

        public string Filename => Id + ".atom.xml";

        public ulong ContentLength => Convert.ToUInt64(Encoding.Default.GetBytes(this.ReadAsStringAsync(CancellationToken.None).Result).Length);

        public bool IsCatalog => false;

        public ContentDisposition ContentDisposition => new ContentDisposition() { FileName = Filename };

        public IReadOnlyList<IResource> GetRoutes()
        {
            return new List<IResource>();
        }

        public async Task<Stream> GetStreamAsync(CancellationToken ct)
        {
            return await Task<Stream>.Run(() =>
            {
                MemoryStream ms = new MemoryStream();
                var sw = XmlWriter.Create(ms);
                Atom10ItemFormatter atomFormatter = new Atom10ItemFormatter(item);
                atomFormatter.WriteTo(sw);
                sw.Flush();
                ms.Seek(0, SeekOrigin.Begin);
                return ms as Stream;
            }, ct);
        }



        public string Identifier
        {
            get
            {
                var identifier = item.ElementExtensions.ReadElementExtensions<string>("identifier", "http://purl.org/dc/elements/1.1/");
                return identifier.Count == 0 ? null : identifier[0];
            }
        }

        public IGeometryObject Geometry => item.FindGeometry();

        public IDictionary<string, object> Properties => item.GetCommonMetadata();

        public bool CanBeRanged => false;

        public IReadOnlyDictionary<string, IAsset> Assets
        {
            get
            {
                Dictionary<string, IAsset> assets = new Dictionary<string, IAsset>();
                Dictionary<string, int> keysCount = item.Links.Where(link => !string.IsNullOrEmpty(link.RelationshipType)).GroupBy(l => l.RelationshipType).ToDictionary(g => g.Key, g => g.Count());
                Dictionary<string, int> keysIndex = item.Links.Where(link => !string.IsNullOrEmpty(link.RelationshipType)).GroupBy(l => l.RelationshipType).ToDictionary(g => g.Key, g => 1);
                foreach (var link in item.Links.Where(link => link.RelationshipType == "enclosure" || link.RelationshipType == "icon").OrderBy(i => i.RelationshipType))
                {
                    string key = link.RelationshipType;
                    if ( keysCount[key] > 1 )
                        key += "-" + keysIndex[key]++;
                    assets.Add(key, new AtomLinkAsset(link, this.AtomItem));
                }
                return assets;
            }
        }

        public ITimePeriod DateTime => new TimeInterval(item.PublishDate.DateTime);

        public Task<Stream> GetStreamAsync(long start, CancellationToken ct, long end = -1)
        {
            throw new NotImplementedException();
        }

        public IReadOnlyList<IResourceLink> GetLinks()
        {
            return item.Links.Select(l => new AtomResourceLink(l)).ToList();
        }
    }
}