using System.Security.Cryptography;

namespace AzureMediaServices.Models
{
    public class Extension
    {
        public string GetWithoutHttp(string smoothStreamingUri)
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

        public static byte[] GetRandomBuffer(int size)
        {
            byte[] randomBytes = new byte[size];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(randomBytes);
            }

            return randomBytes;
        }
    }
}