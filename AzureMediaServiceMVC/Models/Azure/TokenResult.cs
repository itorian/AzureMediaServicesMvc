using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;

namespace AzureMediaServiceMVC.Models
{
    public class TokenResult
    {
        public string TokenString { get; set; }
        public TokenType TokenType { get; set; }
        public bool IsTokenKeySymmetric { get; set; }
        public ContentKeyType ContentKeyType { get; set; }
        public ContentKeyDeliveryType ContentKeyDeliveryType { get; set; }
    }
}