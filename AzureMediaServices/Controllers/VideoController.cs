using AzureMediaPortal.Models;
using AzureMediaServices.Models;
using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;
using Microsoft.WindowsAzure.MediaServices.Client.DynamicEncryption;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;

namespace AzureMediaServices.Controllers
{
    public class VideoController : Controller
    {
        private static readonly string storageConnectionString = ConfigurationManager.AppSettings["StorageConnectionString"];
        private static readonly string storageContainerReference = ConfigurationManager.AppSettings["StorageContainerReference"];
        private static readonly string mediaAccountName = ConfigurationManager.AppSettings["MediaAccountName"];
        private static readonly string mediaAccountKey = ConfigurationManager.AppSettings["MediaAccountKey"];
        private static readonly string storageAccountName = ConfigurationManager.AppSettings["StorageAccountName"];
        private static readonly string storageAccountKey = ConfigurationManager.AppSettings["StorageAccountKey"];

        // A Uri describing the issuer of the token.  
        // Must match the value in the token for the token to be considered valid.
        private static readonly Uri _sampleIssuer = new Uri(ConfigurationManager.AppSettings["Issuer"]);

        // The Audience or Scope of the token.  
        // Must match the value in the token for the token to be considered valid.
        private static readonly Uri _sampleAudience = new Uri(ConfigurationManager.AppSettings["Audience"]);

        private AzureMediaServicesContext db = new AzureMediaServicesContext();
        private static readonly CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);
        private static readonly bool useEncryption = true;

        public ActionResult Index()
        {
            var model = new List<VideoViewModel>();

            var videos = db.Videos.OrderByDescending(o => o.Id).ToList();
            foreach (var video in videos)
            {
                var viewModel = new VideoViewModel();
                viewModel.Id = video.Id;
                viewModel.EncodedAssetId = video.EncodedAssetId;
                viewModel.IsEncrypted = video.IsEncrypted;
                viewModel.LocatorUri = video.LocatorUri;

                // If encrypted content, then get token to play
                if (video.IsEncrypted)
                {
                    IAsset asset = GetAssetById(video.EncodedAssetId);
                    IContentKey key = CreateEnvelopeTypeContentKey(asset);
                    viewModel.Token = GenerateToken(key);
                    //viewModel.Token = "Bearer urn%3amicrosoft%3aazure%3amediaservices%3acontentkeyidentifier=65fa832a-4f75-48b0-8085-6176f94f3cc7&Audience=urn%3atestwebsite&ExpiresOn=1486811210&Issuer=http%3a%2f%2ftest.com%2f&HMACSHA256=QuAv5Gy64P2hfKAXh36EeAD87qKvYupxB9ohO6WNzbU%3d"; // TODO: Create new token from provider
                }

                model.Add(viewModel);
            }

            return View(model);
        }

        public ActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public ActionResult SetMetadata(int blocksCount, string fileName, long fileSize)
        {
            var container = CloudStorageAccount.Parse(storageConnectionString).CreateCloudBlobClient().GetContainerReference(storageContainerReference);

            container.CreateIfNotExists();
            var fileToUpload = new CloudFile()
            {
                BlockCount = blocksCount,
                FileName = fileName,
                Size = fileSize,
                BlockBlob = container.GetBlockBlobReference(fileName),
                StartTime = DateTime.Now,
                IsUploadCompleted = false,
                UploadStatusMessage = string.Empty
            };
            Session.Add("CurrentFile", fileToUpload);
            return Json(true);
        }

        [HttpPost]
        [ValidateInput(false)]
        public ActionResult UploadChunk(int id)
        {
            HttpPostedFileBase request = Request.Files["Slice"];
            byte[] chunk = new byte[request.ContentLength];
            request.InputStream.Read(chunk, 0, Convert.ToInt32(request.ContentLength));
            JsonResult returnData = null;
            string fileSession = "CurrentFile";
            if (Session[fileSession] != null)
            {
                CloudFile model = (CloudFile)Session[fileSession];
                returnData = UploadCurrentChunk(model, chunk, id);
                if (returnData != null)
                {
                    return returnData;
                }
                if (id == model.BlockCount)
                {
                    return CommitAllChunks(model);
                }
            }
            else
            {
                returnData = Json(new
                {
                    error = true,
                    isLastBlock = false,
                    message = string.Format(CultureInfo.CurrentCulture, "Failed to Upload file.", "Session Timed out")
                });
                return returnData;
            }
            return Json(new { error = false, isLastBlock = false, message = string.Empty });
        }

        private JsonResult UploadCurrentChunk(CloudFile model, byte[] chunk, int id)
        {
            using (var chunkStream = new MemoryStream(chunk))
            {
                var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        string.Format(CultureInfo.InvariantCulture, "{0:D4}", id)));
                try
                {
                    model.BlockBlob.PutBlock(
                        blockId,
                        chunkStream, null, null,
                        new BlobRequestOptions()
                        {
                            RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(10), 3)
                        },
                        null);
                    return null;
                }
                catch (StorageException e)
                {
                    Session.Remove("CurrentFile");
                    model.IsUploadCompleted = true;
                    model.UploadStatusMessage = "Failed to Upload file. Exception - " + e.Message;
                    return Json(new { error = true, isLastBlock = false, message = model.UploadStatusMessage });
                }
            }
        }

        private ActionResult CommitAllChunks(CloudFile model)
        {
            model.IsUploadCompleted = true;
            bool errorInOperation = false;
            try
            {
                var blockList = Enumerable.Range(1, (int)model.BlockCount).ToList<int>().ConvertAll(rangeElement => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0:D4}", rangeElement))));
                model.BlockBlob.PutBlockList(blockList);
                var duration = DateTime.Now - model.StartTime;
                float fileSizeInKb = model.Size / 1024;
                string fileSizeMessage = fileSizeInKb > 1024 ? string.Concat((fileSizeInKb / 1024).ToString(CultureInfo.CurrentCulture), " MB") : string.Concat(fileSizeInKb.ToString(CultureInfo.CurrentCulture), " KB");
                model.UploadStatusMessage = string.Format(CultureInfo.CurrentCulture, "File of size {0} took {1} seconds to upload.", fileSizeMessage, duration.TotalSeconds);

                IAsset mediaServiceAsset = CreateMediaAsset(model);
                model.AssetId = mediaServiceAsset.Id;
            }
            catch (StorageException e)
            {
                model.UploadStatusMessage = "Failed to upload file. Exception - " + e.Message;
                errorInOperation = true;
            }
            finally
            {
                Session.Remove("CurrentFile");
            }
            return Json(new
            {
                error = errorInOperation,
                isLastBlock = model.IsUploadCompleted,
                message = model.UploadStatusMessage,
                assetId = model.AssetId
            });
        }

        private IAsset CreateMediaAsset(CloudFile model)
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

        [HttpPost]
        public ActionResult EncodeToAdaptiveBitrateMP4s(string assetId)
        {
            // Note: You need atleast 1 reserve streaming unit for dynamic packaging of encoded media. If you don't have that, you can't see video file playing.
            try
            {
                IAsset inputAsset = GetAssetById(assetId);

                // Without preset (say default preset), works very well
                //IJob job = context.Jobs.CreateWithSingleTask(MediaProcessorNames.AzureMediaEncoder,
                //    MediaEncoderTaskPresetStrings.H264AdaptiveBitrateMP4Set720p,
                //    asset,
                //    "UploadedVideo-" + Guid.NewGuid().ToString().ToLower() + "-Adaptive-Bitrate-MP4",
                //    AssetCreationOptions.None);
                //job.Submit();
                //IAsset encodedOutputAsset = job.OutputMediaAssets[0];
                //string assetDetails = "MediaServiceFileName:" + encodedOutputAsset.Name + ", MediaServiceContainerUri:" 
                //    + encodedOutputAsset.Uri + ", AssetId:" + encodedOutputAsset.Id;


                //// XML Preset
                IJob job = context.Jobs.Create(inputAsset.Name);
                IMediaProcessor processor = GetLatestMediaProcessorByName("Media Encoder Standard");
                string configuration = System.IO.File.ReadAllText(HttpContext.Server.MapPath("~/MediaServicesCustomPreset.xml"));
                ITask task = job.Tasks.AddNew(inputAsset.Name + "- encoding task", processor, configuration, TaskOptions.None);
                task.InputAssets.Add(inputAsset);
                task.OutputAssets.AddNew(inputAsset.Name + "-Adaptive-Bitrate-MP4", AssetCreationOptions.StorageEncrypted);
                job.Submit();
                IAsset encodedOutputAsset = job.OutputMediaAssets[0];
                string assetDetails = "MediaServiceFileName:" + encodedOutputAsset.Name + ", MediaServiceContainerUri:"
                    + encodedOutputAsset.Uri + ", AssetId:" + encodedOutputAsset.Id;

                return Json(new
                {
                    error = false,
                    message = "Encoding scheduled with Job Id " + job.Id + ". Encoded output Asset Id: " + encodedOutputAsset.Id,
                    assetId = encodedOutputAsset.Id,
                    jobId = job.Id
                });
            }
            catch (Exception)
            {
                return Json(new
                {
                    error = true,
                    message = "Error occured in encoding."
                });
            }
        }

        [HttpPost]
        public ActionResult ProcessPolicyAndEncryption(string assetId)
        {
            IAsset asset = GetAssetById(assetId);
            IContentKey key = CreateEnvelopeTypeContentKey(asset);

            string token = string.Empty;

            if (useEncryption)
            {
                token = GenerateToken(key);
            }
            else
            {
                // No restriction
                AddOpenAuthorizationPolicy(key);
            }

            // Set asset delivery policy
            CreateAssetDeliveryPolicy(asset, key);

            // Generate Streaming URL
            string locator = GetWithoutHttp(GetStreamingOriginLocator(asset)) + "/manifest";

            // Update asset in database
            AzureMediaServicesContext db = new AzureMediaServicesContext();
            var video = new Video();
            video.EncodedAssetId = assetId;
            video.LocatorUri = locator;

            if (useEncryption)
            {
                // Update encrypted=true video
                video.IsEncrypted = true;
                db.Videos.Add(video);
                db.SaveChanges();

                return Json(new
                {
                    error = false,
                    message = "Congratulations! Video is available for clear key (AES) encrypted streaming.",
                    encrypted = true,
                    assetId = assetId,
                    locator = locator,
                    token = token
                });
            }

            // Update encrypted=false video
            video.IsEncrypted = false;
            db.Videos.Add(video);
            db.SaveChanges();

            return Json(new
            {
                error = false,
                message = "Congatulations! Video is available to stream without encryption.",
                encrypted = false,
                assetId = assetId,
                locator = locator
            });
        }

        // Clear Key Encryption (aka AES)
        public string GenerateToken(IContentKey key)
        {
            string tokenTemplateString = AddTokenRestrictedAuthorizationPolicy(key);
            TokenRestrictionTemplate tokenTemplate = TokenRestrictionTemplateSerializer.Deserialize(tokenTemplateString);
            Guid rawkey = EncryptionUtils.GetKeyIdAsGuid(key.Id);
            return "Bearer " + TokenRestrictionTemplateSerializer.GenerateTestToken(tokenTemplate, null, rawkey, DateTime.UtcNow.AddDays(1));
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
            // Create envelope encryption content key
            Guid keyId = Guid.NewGuid();
            byte[] contentKey = GetRandomBuffer(16);

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

            string envelopeEncryptionIV = Convert.ToBase64String(GetRandomBuffer(16));

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

        static private string GenerateTokenRequirements()
        {
            TokenRestrictionTemplate template = new TokenRestrictionTemplate(TokenType.SWT);

            template.PrimaryVerificationKey = new SymmetricVerificationKey();
            template.AlternateVerificationKeys.Add(new SymmetricVerificationKey());
            template.Audience = _sampleAudience.ToString();
            template.Issuer = _sampleIssuer.ToString();

            template.RequiredClaims.Add(TokenClaim.ContentKeyIdentifierClaim);

            return TokenRestrictionTemplateSerializer.Serialize(template);
        }

        static private byte[] GetRandomBuffer(int size)
        {
            byte[] randomBytes = new byte[size];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(randomBytes);
            }

            return randomBytes;
        }

        private static IMediaProcessor GetLatestMediaProcessorByName(string mediaProcessorName)
        {
            var processor = context.MediaProcessors.Where(p => p.Name == mediaProcessorName).
            ToList().OrderBy(p => new Version(p.Version)).LastOrDefault();

            if (processor == null)
                throw new ArgumentException(string.Format("Unknown media processor", mediaProcessorName));

            return processor;
        }

        private string GetWithoutHttp(string smoothStreamingUri)
        {
            if (smoothStreamingUri.StartsWith("http:"))
            {
                smoothStreamingUri = smoothStreamingUri.Substring("http:".Length);
            }

            if (smoothStreamingUri.StartsWith("https:"))
            {
                smoothStreamingUri = smoothStreamingUri.Substring("https:".Length);
            }

            return smoothStreamingUri;
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

        // Get encoding job status
        public string GetEncodingJobStatus(string jobId)
        {
            StringBuilder builder = new StringBuilder();
            IJob job = GetJob(jobId);

            Debug.WriteLine("Job ID :" + job.Id);
            Debug.WriteLine("Job Name :" + job.Name);
            Debug.WriteLine("Job State :" + job.State.ToString());
            Debug.WriteLine("Job Start Time :" + job.StartTime.ToString());

            if (job.State == JobState.Error)
            {
                builder.Append("Error Details: \n");
                foreach (ITask task in job.Tasks)
                {
                    foreach (ErrorDetail detail in task.ErrorDetails)
                    {
                        builder.AppendLine("Task Id: " + task.Id);
                        builder.AppendLine("Error Code: " + detail.Code);
                        builder.AppendLine("Error Message: " + detail.Message + "\n");
                    }
                }
                Debug.WriteLine(builder);
                return "Error";
            }

            return job.State.ToString();
        }

        static IJob GetJob(string jobId)
        {
            var jobInstance =
                from j in context.Jobs
                where j.Id == jobId
                select j;
            IJob job = jobInstance.FirstOrDefault();
            return job;
        }

        public ActionResult Delete(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Video video = db.Videos.Find(id);
            if (video == null)
            {
                return HttpNotFound();
            }
            return View(video);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteConfirmed(int id)
        {
            Video video = db.Videos.Find(id);
            db.Videos.Remove(video);
            db.SaveChanges();

            // Delete from Media Service (Asset as well as storage)
            DeleteMediaService(video);

            return RedirectToAction("Index");
        }

        private void DeleteMediaService(Video video)
        {
            string assetId = video.EncodedAssetId;
            IAsset asset = GetAssetFromDatabase(assetId);

            // Now delete the asset
            asset.Delete();
        }

        private IAsset GetAssetFromDatabase(string assetId)
        {
            return context.Assets.Where(a => a.Id == assetId).FirstOrDefault();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}