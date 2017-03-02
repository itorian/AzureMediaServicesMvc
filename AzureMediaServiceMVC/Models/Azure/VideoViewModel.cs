using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace AzureMediaServiceMVC.Models
{
    public class VideoViewModel
    {
        public int Id { get; set; }
        public string EncodedAssetId { get; set; }
        public string LocatorUri { get; set; }
        public bool IsEncrypted { get; set; }
        public string Token { get; set; }
        public string Status { get; set; }
    }
}