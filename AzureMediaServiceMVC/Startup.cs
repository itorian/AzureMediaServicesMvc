using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(AzureMediaServiceMVC.Startup))]
namespace AzureMediaServiceMVC
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            ConfigureAuth(app);
        }
    }
}
