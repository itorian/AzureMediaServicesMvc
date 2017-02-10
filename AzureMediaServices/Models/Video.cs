namespace AzureMediaServices.Models
{
    public class Video
    {
        public int Id { get; set; }
        public string EncodedAssetId { get; set; }
        public string LocatorUri { get; set; }
        public bool IsEncrypted { get; set; }
    }
}