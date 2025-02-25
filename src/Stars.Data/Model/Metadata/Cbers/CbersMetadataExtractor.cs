using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using Stac;
using Stac.Extensions.Eo;
using Stac.Extensions.Processing;
using Stac.Extensions.Sat;
using Stac.Extensions.View;
using Terradue.Stars.Interface;
using Terradue.Stars.Interface.Supplier.Destination;
using Terradue.Stars.Services.Model.Stac;
using Terradue.Stars.Geometry.GeoJson;

namespace Terradue.Stars.Data.Model.Metadata.Cbers
{
    public class CbersMetadataExtractor : MetadataExtraction
    {
        private Regex identifierRegex = new Regex(@"(?'id1'CBERS_4A?_(?'type'[^_]+)_\d{8}_\d{3}_\d{3}_L\d)(_LEFT|RIGHT)?(?'id2'_BAND(?'band'\d+))");
        private Regex bandKeyRegex = new Regex(@"band-\d+");
        private Regex utmZoneRegex = new Regex(@"(?'num'\d+)(?'hem'[NS])");

        public static XmlSerializer metadataSerializer = new XmlSerializer(typeof(Schemas.Metadata));

        public override string Label => "China-Brazil Earth Resources Satellite-4A (INPE) mission product metadata extractor";

        public CbersMetadataExtractor(ILogger<CbersMetadataExtractor> logger, IResourceServiceProvider resourceServiceProvider) : base(logger, resourceServiceProvider)
        {
        }

        public override bool CanProcess(IResource route, IDestination destination)
        {
            IItem item = route as IItem;
            if (item == null) return false;
            try
            {
                IAsset metadataAsset = GetMetadataAsset(item);
                Match identifierMatch = identifierRegex.Match(Path.GetFileName(metadataAsset.Uri.OriginalString));
                if (!identifierMatch.Success)
                    throw new Exception("No metadata file found");

                string typeStr = identifierMatch.Groups["type"].Value;
                Cbers4MetadataType type;

                switch (typeStr)
                {
                    case "AWFI":
                        type = Cbers4MetadataType.Awfi;
                        break;
                    case "MUX":
                        type = Cbers4MetadataType.Mux;
                        break;
                    case "PAN5M":
                    case "PAN10M":
                        type = Cbers4MetadataType.Pan;
                        break;
                    case "WFI":
                        type = Cbers4MetadataType.Wfi;
                        break;
                    case "WPM":
                        type = Cbers4MetadataType.Wpm;
                        break;
                    default:
                        throw new InvalidOperationException(String.Format("Unknown metadata/band type: {0}", typeStr));
                }


                Schemas.Metadata metadata = ReadMetadata(metadataAsset).GetAwaiter().GetResult();

                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        protected override async Task<StacNode> ExtractMetadata(IItem item, string suffix)
        {
            IAsset metadataAsset = GetMetadataAsset(item);
            Schemas.Metadata metadata = await ReadMetadata(metadataAsset);

            Dictionary<string, string> bands = new Dictionary<string, string>();
            FindBands(metadata.image, bands);
            if (metadata.leftCamera != null) FindBands(metadata.leftCamera.image, bands, "-left");
            if (metadata.rightCamera != null) FindBands(metadata.rightCamera.image, bands, "-right");

            StacItem stacItem = CreateStacItem(metadata, bands);

            AddAssets(stacItem, item, metadata, bands);

            AddEoStacExtension(metadata, stacItem);
            AddSatStacExtension(metadata, stacItem);
            AddProjStacExtension(metadata, stacItem);
            AddViewStacExtension(metadata, stacItem);
            AddProcessingStacExtension(metadata, stacItem);
            FillBasicsProperties(metadata, stacItem.Properties);
            AddOtherProperties(metadata, stacItem.Properties);

            return StacItemNode.Create(stacItem, item.Uri);;
        }

        internal virtual StacItem CreateStacItem(Schemas.Metadata metadata, Dictionary<string, string> bands)
        {

            string identifier = null;

            if (bands.Count == 0)
            {
                throw new InvalidOperationException("No band information found");
            }
            else
            {
                bool single = true;   // single has to be true if single band or left/right pair of same band
                if (bands.Count == 1) single = true;
                else
                {
                    string b = null;
                    foreach (string key in bands.Keys)
                    {
                        Match bandKeyMatch = bandKeyRegex.Match(key);
                        if (b == null)
                        {
                            b = bandKeyMatch.Value;
                        }
                        else if (bandKeyMatch.Value != b)
                        {
                            single = false;
                            break;
                        }

                    }
                }
                if (single)
                {
                    string bandName = bands.First().Value;
                    Match identifierMatch = identifierRegex.Match(bandName);
                    if (!identifierMatch.Success)
                    {
                        throw new InvalidOperationException(String.Format("Identifier not recognised from band name: {0}", bandName));
                    }
                    identifier = String.Format("{0}{1}", identifierMatch.Groups["id1"].Value, identifierMatch.Groups["id2"].Value);
                }
                else
                {
                    foreach (string key in bands.Keys)
                    {
                        string bandName = bands[key];
                        Match identifierMatch = identifierRegex.Match(bandName);
                        if (!identifierMatch.Success)
                        {
                            throw new InvalidOperationException(String.Format("Identifier not recognised from band name: {0}", bandName));
                        }
                        identifier = identifierMatch.Groups["id1"].Value;
                        break;
                    }
                }
            }

            StacItem stacItem = new StacItem(identifier, GetGeometry(metadata), GetCommonMetadata(metadata));

            return stacItem;
        }

        private void FindBands(Schemas.prdfImage image, Dictionary<string, string> bands, string suffix = "")
        {
            if (image == null) return;

            if (image.band0 != null) bands[String.Format("band-0{0}", suffix)] = image.band0;
            if (image.band1 != null) bands[String.Format("band-1{0}", suffix)] = image.band1;
            if (image.band2 != null) bands[String.Format("band-2{0}", suffix)] = image.band2;
            if (image.band3 != null) bands[String.Format("band-3{0}", suffix)] = image.band3;
            if (image.band4 != null) bands[String.Format("band-4{0}", suffix)] = image.band4;
            if (image.band5 != null) bands[String.Format("band-5{0}", suffix)] = image.band5;
            if (image.band6 != null) bands[String.Format("band-6{0}", suffix)] = image.band6;
            if (image.band7 != null) bands[String.Format("band-7{0}", suffix)] = image.band7;
            if (image.band8 != null) bands[String.Format("band-8{0}", suffix)] = image.band8;
            if (image.band13 != null) bands[String.Format("band-13{0}", suffix)] = image.band13;
            if (image.band14 != null) bands[String.Format("band-14{0}", suffix)] = image.band14;
            if (image.band15 != null) bands[String.Format("band-15{0}", suffix)] = image.band15;
            if (image.band16 != null) bands[String.Format("band-16{0}", suffix)] = image.band16;
        }


        private GeoJSON.Net.Geometry.IGeometryObject GetGeometry(Schemas.Metadata metadata)
        {
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);
            GeoJSON.Net.Geometry.LineString lineString = new GeoJSON.Net.Geometry.LineString(
                new GeoJSON.Net.Geometry.Position[] {
                    new GeoJSON.Net.Geometry.Position(Double.Parse(image.boundingBox.LL.latitude), Double.Parse(image.boundingBox.LL.longitude)),
                    new GeoJSON.Net.Geometry.Position(Double.Parse(image.boundingBox.LR.latitude), Double.Parse(image.boundingBox.LR.longitude)),
                    new GeoJSON.Net.Geometry.Position(Double.Parse(image.boundingBox.UR.latitude), Double.Parse(image.boundingBox.UR.longitude)),
                    new GeoJSON.Net.Geometry.Position(Double.Parse(image.boundingBox.UL.latitude), Double.Parse(image.boundingBox.UL.longitude)),
                    new GeoJSON.Net.Geometry.Position(Double.Parse(image.boundingBox.LL.latitude), Double.Parse(image.boundingBox.LL.longitude)),
                }
            );
            return new GeoJSON.Net.Geometry.Polygon(new GeoJSON.Net.Geometry.LineString[] { lineString }).NormalizePolygon();
        }


        protected virtual IAsset GetMetadataAsset(IItem item)
        {
            IAsset metadataAsset = FindFirstAssetFromFileNameRegex(item, @"CBERS_4A?.*\d\.xml$");
            if (metadataAsset == null)
            {
                throw new FileNotFoundException(String.Format("Unable to find the metadata file asset"));
            }
            return metadataAsset;
        }

        public virtual async Task<Schemas.Metadata> ReadMetadata(IAsset metadataAsset)
        {
            logger.LogDebug("Opening metadata file {0}", metadataAsset.Uri);

            using (var stream = await resourceServiceProvider.GetAssetStreamAsync(metadataAsset, System.Threading.CancellationToken.None))
            {
                var reader = XmlReader.Create(stream);
                logger.LogDebug("Deserializing metadata file {0}", metadataAsset.Uri);

                return (Schemas.Metadata)metadataSerializer.Deserialize(reader);
            }
        }


        private string GetProcessingLevel(Schemas.Metadata metadata)
        {
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);
            return String.Format("L{0}", image.level);
        }

        private IDictionary<string, object> GetCommonMetadata(Schemas.Metadata metadata)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            FillDateTimeProperties(metadata, properties);
            // TODO Licensing
            // TODO Provider
            FillInstrument(metadata, properties);
            FillBasicsProperties(metadata, properties);

            return properties;
        }

        private void FillDateTimeProperties(Schemas.Metadata metadata, Dictionary<string, object> properties)
        {
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);
            CultureInfo provider = CultureInfo.InvariantCulture;
            DateTime startDate = DateTime.MinValue;
            bool hasStartDate = DateTime.TryParse(image.timeStamp.begin, null, DateTimeStyles.AssumeUniversal, out startDate);
            DateTime endDate = startDate;
            bool hasEndDate = DateTime.TryParse(image.timeStamp.end, null, DateTimeStyles.AssumeUniversal, out endDate);
            DateTime centerDate = startDate;
            bool hasCenterDate = DateTime.TryParse(image.timeStamp.center, null, DateTimeStyles.AssumeUniversal, out centerDate);

            if (hasStartDate && hasEndDate)
            {
                properties["start_datetime"] = startDate.ToUniversalTime();
                properties["end_datetime"] = endDate.ToUniversalTime();
                properties["datetime"] = centerDate.ToUniversalTime();
            }
            else if (hasStartDate)
            {
                properties["datetime"] = startDate.ToUniversalTime();
            }

            DateTime createdDate = DateTime.MinValue;

            bool hasCreatedDate = DateTime.TryParse(image.processingTime, null, DateTimeStyles.AssumeUniversal, out createdDate);

            if (hasCreatedDate)
            {
                properties["created"] = createdDate.ToUniversalTime();
            }

            properties["updated"] = DateTime.UtcNow;
        }


        private void FillInstrument(Schemas.Metadata metadata, Dictionary<string, object> properties)
        {
            // platform & constellation
            Schemas.prdfSatellite satellite = (metadata.leftCamera == null ? metadata.satellite : metadata.leftCamera.satellite);
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);

            properties["platform"] = String.Format("{0}-{1}", satellite.name, satellite.number).ToLower();
            properties["mission"] = properties["platform"];
            properties["instruments"] = new string[] { satellite.instrument.Value.ToLower() };
            properties["sensor_type"] = "optical";
            if (Double.TryParse(image.verticalPixelSize, out double gsd))
            {
                properties["gsd"] = gsd;
            }
        }

        private void FillBasicsProperties(Schemas.Metadata metadata, IDictionary<String, object> properties)
        {
            Schemas.prdfSatellite satellite = (metadata.leftCamera == null ? metadata.satellite : metadata.leftCamera.satellite);
            CultureInfo culture = new CultureInfo("fr-FR");
            properties["title"] = String.Format("{0} {1} {2}",
                String.Format("{0}-{1}", satellite.name.ToUpper(), satellite.number.ToUpper()),
                GetProcessingLevel(metadata),
                properties.GetProperty<DateTime>("datetime").ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", culture)
            );
        }

        private void AddOtherProperties(Schemas.Metadata metadata, IDictionary<String, object> properties)
        {
            if (IncludeProviderProperty)
            {
                AddSingleProvider(
                    properties,
                    "INPE/CAST", 
                    "The China-Brazil Earth Resources Satellite mission is to provide remote sensing images to observe and monitor vegetation - especially deforestation in the Amazon region - the monitoring of water resources, agriculture, urban growth, land use and education.",
                    new StacProviderRole[] { StacProviderRole.producer, StacProviderRole.processor, StacProviderRole.licensor },
                    new Uri("http://www.dgi.inpe.br/en")
                );
            }
        }


        private void AddEoStacExtension(Schemas.Metadata metadata, StacItem stacItem)
        {
            var eo = stacItem.EoExtension();
            eo.Bands = stacItem.Assets.Values.Where(a => a.EoExtension().Bands != null).SelectMany(a => a.EoExtension().Bands).ToArray();
        }


        private void AddSatStacExtension(Schemas.Metadata metadata, StacItem stacItem)
        {
            var sat = new SatStacExtension(stacItem);
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);
            sat.OrbitState = image.orbitDirection.ToLower();
            if (Int64.TryParse(image.path, out long path) && Int64.TryParse(image.row, out long row))
            {
                sat.AbsoluteOrbit = Convert.ToInt32(1000 * path + row);
            }
            // sat.RelativeOrbit = 
            // sat.PlatformInternationalDesignator = 
        }


        private void AddProjStacExtension(Schemas.Metadata metadata, StacItem stacItem)
        {
            /*            if (metadata.mapProjection != "UTM" || metadata.Zone_Number == null) return;
                        Match utmZoneMatch = utmZoneRegex.Match(metadata.image.projectionName);
                        Console.WriteLine("ZONE: {0} {1}", metadata.Zone_Number, utmZoneMatch.Success);
                        if (!utmZoneMatch.Success) return;


                        ProjectionStacExtension proj = stacItem.ProjectionExtension();
                        //proj.Wkt2 = ProjNet.CoordinateSystems.GeocentricCoordinateSystem.WGS84.WKT;

                        int zone = Int32.Parse(utmZoneMatch.Groups["num"].Value);
                        bool north = utmZoneMatch.Groups["hem"].Value == "N";
                        ProjectedCoordinateSystem utm = ProjectedCoordinateSystem.WGS84_UTM(zone, north);
                        proj.SetCoordinateSystem(utm);*/
        }


        private void AddViewStacExtension(Schemas.Metadata metadata, StacItem stacItem)
        {
            var view = new ViewStacExtension(stacItem);
            Schemas.prdfImage image = (metadata.leftCamera == null ? metadata.image : metadata.leftCamera.image);
            if (Double.TryParse(image.offNadirAngle, out double offNadir))
            {
                view.OffNadir = offNadir / 1000;
            }
            if (Double.TryParse(image.sunPosition.sunAzimuth, out double sunAzimuth))
            {
                view.SunAzimuth = sunAzimuth;
            }
            if (Double.TryParse(image.sunPosition.elevation, out double sunElevation))
            {
                view.SunElevation = sunElevation;
            }
        }


        private void AddProcessingStacExtension(Schemas.Metadata metadata, StacItem stacItem)
        {
            var proc = stacItem.ProcessingExtension();
            proc.Level = GetProcessingLevel(metadata);
        }


        protected void AddAssets(StacItem stacItem, IItem item, Schemas.Metadata metadata, Dictionary<string, string> bands)
        {
            foreach (string key in bands.Keys)
            {
                string bandFile = Path.GetFileName(bands[key]);

                IAsset bandAsset = FindFirstAssetFromFileNameRegex(item, String.Format("{0}$", bandFile)); //.Replace(".", @"\.")
                if (bandAsset == null)
                    throw new FileNotFoundException(string.Format("Band file declared in metadata, but not present '{0}'", bandFile));

                AddBandAsset(stacItem, key, bandAsset, metadata);
            }

            IAsset metadataAsset = GetMetadataAsset(item);
            stacItem.Assets.Add("metadata", StacAsset.CreateMetadataAsset(stacItem, metadataAsset.Uri, new ContentType(MimeTypes.GetMimeType(metadataAsset.Uri.OriginalString)), "Metadata file"));
            stacItem.Assets["metadata"].Properties.AddRange(metadataAsset.Properties);
            IAsset additionalMetadataAsset = FindFirstAssetFromFileNameRegex(item, @"VRSS-[12]_.*ADDITION\.xml$");
            if (additionalMetadataAsset != null){
                stacItem.Assets.Add("metadata-addition", StacAsset.CreateMetadataAsset(stacItem, additionalMetadataAsset.Uri, new ContentType(MimeTypes.GetMimeType(additionalMetadataAsset.Uri.OriginalString)), "Additional metadata"));
                stacItem.Assets["metadata-addition"].Properties.AddRange(additionalMetadataAsset.Properties);
            }
        }


        private void AddBandAsset(StacItem stacItem, string assetKey, IAsset imageAsset, Schemas.Metadata metadata)
        {
            StacAsset stacAsset = StacAsset.CreateDataAsset(stacItem, imageAsset.Uri, new ContentType(MimeTypes.GetMimeType(imageAsset.Uri.OriginalString)), "Image file");
            stacAsset.Properties.AddRange(imageAsset.Properties);
            double waveLength = 0;
            EoBandCommonName commonName = new EoBandCommonName();
            bool notFound = false;

            Schemas.prdfSatellite satellite = (metadata.leftCamera == null ? metadata.satellite : metadata.leftCamera.satellite);
            if (satellite.instrument.Value == "WPM")
            {
                switch (assetKey)
                {
                    case "band-0":   // WPM
                        waveLength = 0.675;
                        commonName = EoBandCommonName.pan;
                        break;
                    case "band-1":   // WPM
                        waveLength = 0.485;
                        commonName = EoBandCommonName.blue;
                        break;
                    case "band-2":   // WPM
                        waveLength = 0.555;
                        commonName = EoBandCommonName.green;
                        break;
                    case "band-3":   // WPM
                        waveLength = 0.660;
                        commonName = EoBandCommonName.red;
                        break;
                    case "band-4":   // WPM
                        waveLength = 0.830;
                        commonName = EoBandCommonName.nir;
                        break;
                }
            }
            else
            {
                switch (assetKey)
                {
                    case "band-1":   // PAN5M
                        waveLength = 0.680;
                        commonName = EoBandCommonName.pan;
                        break;
                    case "band-2":   // PAN10M
                        waveLength = 0.555;
                        commonName = EoBandCommonName.green;
                        break;
                    case "band-3":   // PAN10M
                        waveLength = 0.660;
                        commonName = EoBandCommonName.red;
                        break;
                    case "band-4":   // PAN10M
                        waveLength = 0.830;
                        commonName = EoBandCommonName.nir;
                        break;
                    case "band-5":   // MUX
                        waveLength = 0.485;
                        commonName = EoBandCommonName.blue;
                        break;
                    case "band-6":   // MUX
                        waveLength = 0.555;
                        commonName = EoBandCommonName.green;
                        break;
                    case "band-7":   // MUX
                        waveLength = 0.660;
                        commonName = EoBandCommonName.red;
                        break;
                    case "band-8":   // MUX
                        waveLength = 0.830;
                        commonName = EoBandCommonName.nir;
                        break;
                    case "band-13":   // AWFI
                    case "band-13-left":   // WFI
                    case "band-13-right":   // WFI
                        waveLength = 0.830;
                        commonName = EoBandCommonName.nir;
                        break;
                    case "band-14":   // AWFI
                    case "band-14-left":   // WFI
                    case "band-14-right":   // WFI
                        waveLength = 0.660;
                        commonName = EoBandCommonName.red;
                        break;
                    case "band-15":   // AWFI
                    case "band-15-left":   // WFI
                    case "band-15-right":   // WFI
                        waveLength = 0.555;
                        commonName = EoBandCommonName.green;
                        break;
                    case "band-16":   // AWFI
                    case "band-16-left":   // WFI
                    case "band-16-right":   // WFI
                        waveLength = 0.485;
                        commonName = EoBandCommonName.blue;
                        break;
                    default:
                        notFound = true;
                        break;
                }
            }

            if (notFound)
            {
                throw new InvalidOperationException(String.Format("Band information not found for {0}", assetKey));
            }

            EoBandObject eoBandObject = new EoBandObject(assetKey, commonName);
            eoBandObject.CenterWavelength = waveLength;

            stacAsset.EoExtension().Bands = new EoBandObject[] { eoBandObject };
            stacItem.Assets.Add(assetKey, stacAsset);
        }

    }


    public enum Cbers4MetadataType
    {
        Awfi,
        Mux,
        Pan,
        Wfi,
        Wpm
    }



}
