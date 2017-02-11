using AzureMediaPortal.Models;
using AzureMediaServices.Models;
using Microsoft.WindowsAzure.MediaServices.Client;
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

        private AzureMediaServicesContext db = new AzureMediaServicesContext();
        private static readonly CloudMediaContext context = new CloudMediaContext(mediaAccountName, mediaAccountKey);
        private static readonly bool useEncryption = true;

        private AzureMediaService AzureMediaService = new AzureMediaService();
        private Extension Extension = new Extension();

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

                // If encrypted content, then generate test token for playback
                if (video.IsEncrypted)
                {
                    IAsset asset = AzureMediaService.GetAssetById(video.EncodedAssetId);
                    IContentKey key = AzureMediaService.CreateEnvelopeTypeContentKey(asset);

                    viewModel.Token = AzureMediaService.GenerateTestToken(key); // Error
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

                IAsset mediaServiceAsset = AzureMediaService.CreateMediaAsset(model);
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

        [HttpPost]
        public ActionResult EncodeToAdaptiveBitrateMP4s(string assetId)
        {
            // Note: You need atleast 1 reserve streaming unit for dynamic packaging of encoded media. If you don't have that, you can't see video file playing.
            try
            {
                IAsset inputAsset = AzureMediaService.GetAssetById(assetId);

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
                IMediaProcessor processor = AzureMediaService.GetLatestMediaProcessorByName("Media Encoder Standard");
                string configuration = System.IO.File.ReadAllText(HttpContext.Server.MapPath("~/MediaServicesCustomPreset.xml"));
                ITask task = job.Tasks.AddNew(inputAsset.Name + "- encoding task", processor, configuration, TaskOptions.None);
                task.InputAssets.Add(inputAsset);
                //task.OutputAssets.AddNew(inputAsset.Name + "-Adaptive-Bitrate-MP4", AssetCreationOptions.StorageEncrypted);
                task.OutputAssets.AddNew(inputAsset.Name + "-Adaptive-Bitrate-MP4", AssetCreationOptions.None);
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
            IAsset asset = AzureMediaService.GetAssetById(assetId);
            IContentKey key = AzureMediaService.CreateEnvelopeTypeContentKey(asset);

            string token = string.Empty;

            if (useEncryption)
            {
                // Clear key encryption (aka AES encryption)
                token = AzureMediaService.GenerateToken(key);
            }
            else
            {
                // No restriction
                AzureMediaService.AddOpenAuthorizationPolicy(key);
            }

            // Set asset delivery policy
            AzureMediaService.CreateAssetDeliveryPolicy(asset, key);

            // Generate locator for streaming
            string locator = Extension.GetWithoutHttp(AzureMediaService.GetStreamingOriginLocator(asset)) + "/manifest";

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

        // Get encoding job status
        public string GetEncodingJobStatus(string jobId)
        {
            StringBuilder builder = new StringBuilder();
            IJob job = AzureMediaService.GetJob(jobId);

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
            AzureMediaService.DeleteMediaService(video);

            return RedirectToAction("Index");
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
