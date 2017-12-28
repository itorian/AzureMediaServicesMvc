using System;
using System.Linq;
using System.Configuration;
using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;
using System.Text;

namespace AzureMediaServiceMVC.Models.Azure
{
    public static class AzureMediaAsset
    {
        //// DEPRECATED method
        //private static readonly string mediaAccountName = ConfigurationManager.AppSettings["MediaAccountName"];
        //private static readonly string mediaAccountKey = ConfigurationManager.AppSettings["MediaAccountKey"];
        //private static readonly CloudMediaContext context = new CloudMediaContext(new MediaServicesCredentials(mediaAccountName, mediaAccountKey));

        //// Azure Active Directory (AAD)
        private static readonly string mediaServiceADTenantDomain = ConfigurationManager.AppSettings["MediaServiceADTenantDomain"];
        private static readonly string mediaServiceADRestApiEndpoint = ConfigurationManager.AppSettings["MediaServiceADRestApiEndpoint"];
        private static readonly string mediaServiceADApplicationId = ConfigurationManager.AppSettings["MediaServiceADApplicationId"];
        private static readonly string mediaServiceADApplicationSecret = ConfigurationManager.AppSettings["MediaServiceADApplicationSecret"];
        private static readonly AzureAdTokenCredentials azureAdTokenCredentials = new AzureAdTokenCredentials(mediaServiceADTenantDomain, new AzureAdClientSymmetricKey(mediaServiceADApplicationId, mediaServiceADApplicationSecret), AzureEnvironments.AzureCloudEnvironment);
        private static readonly AzureAdTokenProvider azureAdTokenProvider = new AzureAdTokenProvider(azureAdTokenCredentials);
        private static readonly CloudMediaContext context = new CloudMediaContext(new Uri(mediaServiceADRestApiEndpoint), azureAdTokenProvider);

        public static string GetTestToken(string assetid, IAsset asset = null)
        {
            if (asset == null)
            {
                asset = GetAssetById(assetid);
            }

            IContentKey key = asset.ContentKeys.FirstOrDefault();

            if (key != null && key.AuthorizationPolicyId != null)
            {
                IContentKeyAuthorizationPolicy policy = context.ContentKeyAuthorizationPolicies.Where(p => p.Id == key.AuthorizationPolicyId).FirstOrDefault();

                if (policy != null)
                {
                    IContentKeyAuthorizationPolicyOption option = null;
                    option = policy.Options.Where(o => (ContentKeyRestrictionType)o.Restrictions.FirstOrDefault().KeyRestrictionType == ContentKeyRestrictionType.TokenRestricted).FirstOrDefault();

                    if (option != null)
                    {
                        string tokenTemplateString = option.Restrictions.FirstOrDefault().Requirements;

                        if (!string.IsNullOrEmpty(tokenTemplateString))
                        {
                            Guid rawkey = EncryptionUtils.GetKeyIdAsGuid(key.Id);
                            TokenRestrictionTemplate tokenTemplate = TokenRestrictionTemplateSerializer.Deserialize(tokenTemplateString);

                            if (tokenTemplate.TokenType == TokenType.SWT) //SWT
                            {
                                return "Bearer " + TokenRestrictionTemplateSerializer.GenerateTestToken(tokenTemplate, null, rawkey, DateTime.UtcNow.AddDays(1));
                            }
                        }
                    }
                }
            }

            return null;
        }

        public static string GetEncodingJobStatus(string jobId)
        {
            StringBuilder builder = new StringBuilder();
            var jobInstance = from j in context.Jobs where j.Id == jobId select j;
            IJob job = jobInstance.FirstOrDefault();

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
                return "Error";
            }

            return job.State.ToString();
        }

        private static IAsset GetAssetById(string assetId)
        {
            IAsset theAsset = (from a in context.Assets
                               where a.Id == assetId
                               select a).FirstOrDefault();
            return theAsset;
        }
    }
}