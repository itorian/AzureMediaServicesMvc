namespace AzureMediaServices.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class assetIdAdded : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.Videos", "AssetId", c => c.String());
        }
        
        public override void Down()
        {
            DropColumn("dbo.Videos", "AssetId");
        }
    }
}
