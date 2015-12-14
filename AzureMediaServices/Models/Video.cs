using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace AzureMediaServices.Models
{
    public class Video
    {
        public int Id { get; set; }
        public string AssetId { get; set; }
        public string VideoURI { get; set; }
    }
}