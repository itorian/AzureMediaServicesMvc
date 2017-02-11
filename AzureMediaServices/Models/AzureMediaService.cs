using AzureMediaPortal.Models;
using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;
using Microsoft.WindowsAzure.MediaServices.Client.DynamicEncryption;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;

namespace AzureMediaServices.Models
{
    public class AzureMediaService
    {
        private static readonly string storageConnectionString = ConfigurationManager.AppSettings["StorageConnectionString"];
        private static readonly string storageContainerReference = ConfigurationManager.AppSettings["StorageContainerReference"];
        private static readonly string mediaAccountName = ConfigurationManager.AppSettings["MediaAccountName"];
        private static readonly string mediaAccountKey = ConfigurationManager.AppSettings["MediaAccountKey"];

        // issuer of the token and audience
        private static readonly Uri _sampleIssuer = new Uri(ConfigurationManager.AppSettings["Issuer"]);
        private static readonly Uri _sampleAudience = new Uri(ConfigurationManager.AppSettings["Audience"]);

        private static readonly CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);

        public IAsset CreateMediaAsset(CloudFile model)
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer mediaBlobContainer = cloudBlobClient.GetContainerReference(storageContainerReference);

            mediaBlobContainer.CreateIfNotExists();

            // Create a new asset.
            IAsset asset = context.Assets.Create("UploadedVideo-" + Guid.NewGuid().ToString().ToLower(), AssetCreationOptions.None);
            IAccessPolicy writePolicy = context.AccessPolicies.Create("writePolicy", TimeSpan.FromMinutes(120), AccessPermissions.Write);
            ILocator destinationLocator = context.Locators.CreateLocator(LocatorType.Sas, asset, writePolicy);

            // Get the asset container URI and copy blobs from mediaContainer to assetContainer.
            Uri uploadUri = new Uri(destinationLocator.Path);
            string assetContainerName = uploadUri.Segments[1];
            CloudBlobContainer assetContainer = cloudBlobClient.GetContainerReference(assetContainerName);
            string fileName = HttpUtility.UrlDecode(Path.GetFileName(model.BlockBlob.Uri.AbsoluteUri));

            var sourceCloudBlob = mediaBlobContainer.GetBlockBlobReference(fileName);
            sourceCloudBlob.FetchAttributes();

            if (sourceCloudBlob.Properties.Length > 0)
            {
                IAssetFile assetFile = asset.AssetFiles.Create(fileName);
                var destinationBlob = assetContainer.GetBlockBlobReference(fileName);

                destinationBlob.DeleteIfExists();
                destinationBlob.StartCopyFromBlob(sourceCloudBlob);
                destinationBlob.FetchAttributes();
                if (sourceCloudBlob.Properties.Length != destinationBlob.Properties.Length)
                    model.UploadStatusMessage += "Failed to copy as Media Asset!";
            }
            destinationLocator.Delete();
            writePolicy.Delete();
            sourceCloudBlob.Delete();  //delete temp blob

            // Refresh the asset.
            asset = context.Assets.Where(a => a.Id == asset.Id).FirstOrDefault();

            var ismAssetFiles = asset.AssetFiles.FirstOrDefault();
            ismAssetFiles.IsPrimary = true;
            ismAssetFiles.Update();

            model.UploadStatusMessage += " Media file uploaded successfully by id: " + asset.Id;
            model.AssetId = asset.Id;

            return asset;
        }

        static public void AddOpenAuthorizationPolicy(IContentKey contentKey)
        {
            // Create ContentKeyAuthorizationPolicy with Open restrictions and create authorization policy             
            IContentKeyAuthorizationPolicy policy = context.ContentKeyAuthorizationPolicies.CreateAsync("Open Authorization Policy").Result;

            List<ContentKeyAuthorizationPolicyRestriction> restrictions = new List<ContentKeyAuthorizationPolicyRestriction>();
            ContentKeyAuthorizationPolicyRestriction restriction =
                new ContentKeyAuthorizationPolicyRestriction
                {
                    Name = "HLS Open Authorization Policy",
                    KeyRestrictionType = (int)ContentKeyRestrictionType.Open,
                    Requirements = null // no requirements needed for HLS
                };

            restrictions.Add(restriction);

            IContentKeyAuthorizationPolicyOption policyOption =
                context.ContentKeyAuthorizationPolicyOptions.Create(
                "policy",
                ContentKeyDeliveryType.BaselineHttp,
                restrictions,
                "");

            policy.Options.Add(policyOption);

            // Add ContentKeyAutorizationPolicy to ContentKey
            contentKey.AuthorizationPolicyId = policy.Id;
            IContentKey updatedKey = contentKey.UpdateAsync().Result;
        }

        static public IContentKey CreateEnvelopeTypeContentKey(IAsset asset)
        {
            // If key already, then return it : abhimanyu
            if (asset.ContentKeys.Count > 0)
                return asset.ContentKeys.FirstOrDefault();

            // Create envelope encryption content key
            Guid keyId = Guid.NewGuid();
            byte[] contentKey = Extension.GetRandomBuffer(16);

            IContentKey key = context.ContentKeys.Create(
                                    keyId,
                                    contentKey,
                                    "ContentKey",
                                    ContentKeyType.EnvelopeEncryption);

            // Associate the key with the asset.
            asset.ContentKeys.Add(key);

            return key;
        }

        static public string GetStreamingOriginLocator(IAsset asset)
        {
            // Get a reference to the streaming manifest file from the collection of files in the asset. 
            var assetFile = asset.AssetFiles.Where(f => f.Name.ToLower().EndsWith(".ism")).FirstOrDefault();

            // A locator expiry can be set to maximum 100 years, using 99 years below.
            TimeSpan daysForWhichStreamingUrlIsActive = DateTime.Now.AddYears(99) - DateTime.Now;
            IAccessPolicy policy = context.AccessPolicies.Create("Streaming policy", daysForWhichStreamingUrlIsActive, AccessPermissions.Read);

            // Create a locator to the streaming content on an origin.
            ILocator originLocator = context.Locators.CreateLocator(LocatorType.OnDemandOrigin, asset, policy, DateTime.UtcNow.AddMinutes(-5));

            // Create a URL to the manifest file. 
            return originLocator.Path + assetFile.Name;
        }

        static public void CreateAssetDeliveryPolicy(IAsset asset, IContentKey key)
        {
            Uri keyAcquisitionUri = key.GetKeyDeliveryUrl(ContentKeyDeliveryType.BaselineHttp);

            string envelopeEncryptionIV = Convert.ToBase64String(Extension.GetRandomBuffer(16));

            // The following policy configuration specifies: key url that will have KID=<Guid> appended to the envelope and
            // the Initialization Vector (IV) to use for the envelope encryption.
            Dictionary<AssetDeliveryPolicyConfigurationKey, string> assetDeliveryPolicyConfiguration =
                new Dictionary<AssetDeliveryPolicyConfigurationKey, string>
            {
                        {AssetDeliveryPolicyConfigurationKey.EnvelopeKeyAcquisitionUrl, keyAcquisitionUri.ToString()}
            };

            IAssetDeliveryPolicy assetDeliveryPolicy =
                context.AssetDeliveryPolicies.Create(
                            "AssetDeliveryPolicy",
                            AssetDeliveryPolicyType.DynamicEnvelopeEncryption,
                            AssetDeliveryProtocol.SmoothStreaming | AssetDeliveryProtocol.HLS | AssetDeliveryProtocol.Dash,
                            assetDeliveryPolicyConfiguration);

            // Add AssetDelivery Policy to the asset
            asset.DeliveryPolicies.Add(assetDeliveryPolicy);
        }

        public string GenerateTestToken(IContentKey key)
        {
            string tokenTemplateString = AddTokenRestrictedAuthorizationPolicy(key);
            TokenRestrictionTemplate tokenTemplate = TokenRestrictionTemplateSerializer.Deserialize(tokenTemplateString);
            Guid rawkey = EncryptionUtils.GetKeyIdAsGuid(key.Id); 
            return "Bearer " + TokenRestrictionTemplateSerializer.GenerateTestToken(tokenTemplate, null, rawkey, DateTime.UtcNow.AddDays(1));
        }

        public static string AddTokenRestrictedAuthorizationPolicy(IContentKey contentKey)
        {
            string tokenTemplateString = GenerateTokenRequirements();

            IContentKeyAuthorizationPolicy policy = context.
                                    ContentKeyAuthorizationPolicies.
                                    CreateAsync("HLS token restricted authorization policy").Result;

            List<ContentKeyAuthorizationPolicyRestriction> restrictions = new List<ContentKeyAuthorizationPolicyRestriction>();

            ContentKeyAuthorizationPolicyRestriction restriction =
                    new ContentKeyAuthorizationPolicyRestriction
                    {
                        Name = "Token Authorization Policy",
                        KeyRestrictionType = (int)ContentKeyRestrictionType.TokenRestricted,
                        Requirements = tokenTemplateString
                    };

            restrictions.Add(restriction);

            //You could have multiple options 
            IContentKeyAuthorizationPolicyOption policyOption =
                context.ContentKeyAuthorizationPolicyOptions.Create(
                    "Token option for HLS",
                    ContentKeyDeliveryType.BaselineHttp,
                    restrictions,
                    null  // no key delivery data is needed for HLS
                    );

            policy.Options.Add(policyOption);

            // Add ContentKeyAutorizationPolicy to ContentKey
            contentKey.AuthorizationPolicyId = policy.Id;
            IContentKey updatedKey = contentKey.UpdateAsync().Result;

            return tokenTemplateString;
        }

        public static string GenerateTokenRequirements()
        {
            TokenRestrictionTemplate template = new TokenRestrictionTemplate(TokenType.SWT);

            template.PrimaryVerificationKey = new SymmetricVerificationKey();
            template.AlternateVerificationKeys.Add(new SymmetricVerificationKey());
            template.Audience = _sampleAudience.ToString();
            template.Issuer = _sampleIssuer.ToString();

            template.RequiredClaims.Add(TokenClaim.ContentKeyIdentifierClaim);

            return TokenRestrictionTemplateSerializer.Serialize(template);
        }

        public static IMediaProcessor GetLatestMediaProcessorByName(string mediaProcessorName)
        {
            var processor = context.MediaProcessors.Where(p => p.Name == mediaProcessorName).
            ToList().OrderBy(p => new Version(p.Version)).LastOrDefault();

            if (processor == null)
                throw new ArgumentException(string.Format("Unknown media processor", mediaProcessorName));

            return processor;
        }

        // Create locator and get urls
        static public string PublishAssetGetURLs(IAsset asset)
        {
            // A locator expiry can be set to maximum 100 years, using 99 years below.
            TimeSpan daysForWhichStreamingUrlIsActive = DateTime.Now.AddYears(99) - DateTime.Now;
            IAccessPolicy videoWatchPolicy = context.AccessPolicies.Create("videoWatchPolicy", daysForWhichStreamingUrlIsActive, AccessPermissions.Read);
            ILocator destinationLocator = context.Locators.CreateLocator(LocatorType.OnDemandOrigin, asset, videoWatchPolicy);

            // Get the asset container URI
            Uri uploadUri = new Uri(destinationLocator.Path);

            // Note: You need atleast 1 reserve streaming unit for dynamic packaging of encoded media. If you don't have that, you can't see video file playing
            return uploadUri.ToString() + asset.Name + ".ism/manifest";
        }

        internal static IAsset GetAssetById(string assetId)
        {
            IAsset theAsset = (from a in context.Assets
                               where a.Id == assetId
                               select a).FirstOrDefault();
            return theAsset;
        }

        public static IJob GetJob(string jobId)
        {
            var jobInstance =
                from j in context.Jobs
                where j.Id == jobId
                select j;
            IJob job = jobInstance.FirstOrDefault();
            return job;
        }

        public void DeleteMediaService(Video video)
        {
            string assetId = video.EncodedAssetId;
            IAsset asset = GetAssetFromDatabase(assetId);

            asset.Delete();
        }

        public IAsset GetAssetFromDatabase(string assetId)
        {
            return context.Assets.Where(a => a.Id == assetId).FirstOrDefault();
        }
    }
}