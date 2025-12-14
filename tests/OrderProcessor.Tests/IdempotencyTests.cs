[Fact]
public void Same_File_Not_Inserted_Twice()
{
    var db = new AppDbContext();
    var hash = "testhash";

    db.ProcessedFiles.Add(new ProcessedFile { Hash = hash });
    db.SaveChanges();

    Assert.Throws<DbUpdateException>(() =>
    {
        db.ProcessedFiles.Add(new ProcessedFile { Hash = hash });
        db.SaveChanges();
    });
}
