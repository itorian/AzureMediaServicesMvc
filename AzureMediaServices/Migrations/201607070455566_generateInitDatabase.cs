namespace AzureMediaServices.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class generateInitDatabase : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.Videos",
                c => new
                    {
                        Id = c.Int(nullable: false, identity: true),
                        AssetId = c.String(),
                        VideoURI = c.String(),
                    })
                .PrimaryKey(t => t.Id);
            
        }
        
        public override void Down()
        {
            DropTable("dbo.Videos");
        }
    }
}
