using AzureMediaPortal.Models;
using AzureMediaServices.Models;
using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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

        private AzureMediaServicesContext db = new AzureMediaServicesContext();

        public ActionResult Index()
        {
            return View(db.Videos.OrderByDescending(o => o.Id).ToList());
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
            CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer mediaBlobContainer = cloudBlobClient.GetContainerReference(storageContainerReference);

            mediaBlobContainer.CreateIfNotExists();

            // Create a new asset.
            IAsset asset = context.Assets.Create("UploadedVideo-" + Guid.NewGuid(), AssetCreationOptions.None);
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

            var ismAssetFiles = asset.AssetFiles.ToList().Where(f => f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (ismAssetFiles.Count() != 1)
                throw new ArgumentException("The asset should have only one, .ism file");

            ismAssetFiles.First().IsPrimary = true;
            ismAssetFiles.First().Update();

            model.UploadStatusMessage += " Media file uploaded successfully by id: " + asset.Id;
            model.AssetId = asset.Id;

            return asset;
        }

        [HttpPost]
        public ActionResult EncodeToAdaptiveBitrateMP4s(string assetId, string fileName)
        {
            // Note: You need atleast 1 reserve streaming unit for dynamic packaging of encoded media. If you don't have that, you can't see video file playing
            try
            {
                CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);
                IAsset asset = GetAssetById(context, assetId);

                IJob job = context.Jobs.CreateWithSingleTask(MediaProcessorNames.AzureMediaEncoder,
                    MediaEncoderTaskPresetStrings.H264AdaptiveBitrateMP4Set720p,
                    asset,
                    "UploadedVideo-" + Guid.NewGuid().ToString() + "-Adaptive-Bitrate-MP4",
                    AssetCreationOptions.None);

                job.Submit();

                IAsset encodedAsset = job.OutputMediaAssets[0];


                //// Encryption (AES or DRM)
                // Encode outputAsset with AES Key
                //string token = AESEncryption.CreateAESEncryption(context, encodedAsset);

                string smoothStreamingUri = PublishAssetGetURLs(encodedAsset, fileName);
                string assetDetails = "MediaServiceFileName:" + encodedAsset.Name + ", MediaServiceContainerUri:" + encodedAsset.Uri + ", AssetId:" + encodedAsset.Id;

                // Save video URI in database
                AzureMediaServicesContext db = new AzureMediaServicesContext();
                Video video = new Video();
                video.Id = 0;
                video.AssetId = encodedAsset.Id;
                video.VideoURI = GetWithoutHttp(smoothStreamingUri);
                db.Videos.Add(video);
                db.SaveChanges();

                return Json(new
                {
                    error = false,
                    message = "Encoding scheduled with Job Id " + job.Id + ". Encoded output Asset Id: " + encodedAsset.Id,
                    assetId = encodedAsset.Id,
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

        private string GetWithoutHttp(string smoothStreamingUri)
        {
            if (smoothStreamingUri.StartsWith("http:"))
            {
                smoothStreamingUri = smoothStreamingUri.Substring("http:".Length);
            }

            return smoothStreamingUri;
        }

        // Create locator and get urls
        static public string PublishAssetGetURLs(IAsset asset, string fileName)
        {
            // A locator expiry can be set to maximum 100 years, using 99 years below.
            DateTime now = DateTime.Now;
            TimeSpan daysForWhichStreamingUrlIsActive = now.AddYears(99) - now;
            CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);
            IAccessPolicy videoWatchPolicy = context.AccessPolicies.Create("videoWatchPolicy", daysForWhichStreamingUrlIsActive, AccessPermissions.Read);
            ILocator destinationLocator = context.Locators.CreateLocator(LocatorType.OnDemandOrigin, asset, videoWatchPolicy);

            // Get urls
            //Uri smoothStreamingUri = asset.GetSmoothStreamingUri();
            //Uri hlsUri = asset.GetHlsUri();
            //Uri mpegDashUri = asset.GetMpegDashUri();
            //return smoothStreamingUri.ToString();


            // Get the asset container URI
            Uri uploadUri = new Uri(destinationLocator.Path);

            // Note: You need atleast 1 reserve streaming unit for dynamic packaging of encoded media. If you don't have that, you can't see video file playing
            return uploadUri.ToString() + fileName + ".ism/Manifest";
        }

        internal static IAsset GetAssetById(CloudMediaContext context, string assetId)
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
            CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);
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
            // Delete from Media Services
            CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);

            string assetId = video.AssetId;
            IAsset asset = GetAsset(context, assetId);

            // Now delete the asset
            asset.Delete();
        }

        private IAsset GetAsset(CloudMediaContext context, string assetId)
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
