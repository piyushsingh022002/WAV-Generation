using System.Diagnostics;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<MongoSettings>(
    builder.Configuration.GetSection("MongoSettings"));
builder.Services.AddSingleton<MongoService>();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();

app.MapPost("/convert", async (
    HttpRequest request,
    IFormFile file,
    MongoService mongoService) =>
{
    if (file == null || Path.GetExtension(file.FileName).ToLower() != ".mp3")
        return Results.BadRequest("Only MP3 files allowed.");

    var tempMp3 = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3");
    var tempWav = Path.ChangeExtension(tempMp3, ".wav");

    await using (var stream = File.Create(tempMp3))
        await file.CopyToAsync(stream);

    var ffmpeg = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -i \"{tempMp3}\" \"{tempWav}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    ffmpeg.Start();
    await ffmpeg.WaitForExitAsync();

    if (!File.Exists(tempWav))
        return Results.Problem("FFmpeg conversion failed.");

    var wavBytes = await File.ReadAllBytesAsync(tempWav);
    await mongoService.SaveConversionAsync(file.FileName);

    File.Delete(tempMp3);
    File.Delete(tempWav);

    return Results.File(wavBytes, "audio/wav", Path.GetFileName(tempWav));
}).DisableAntiforgery();


app.Run();

// public record MongoSettings(string ConnectionString, string DatabaseName);
public record MongoSettings
{
    public string ConnectionString { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
};

public class MongoService
{
    private readonly IMongoCollection<ConversionLog> _collection;

    public MongoService(IOptions<MongoSettings> settings)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var db = client.GetDatabase(settings.Value.DatabaseName);
        _collection = db.GetCollection<ConversionLog>("conversions");
    }

    public Task SaveConversionAsync(string filename)
    {
        var log = new ConversionLog
        {
            FileName = filename,
            ConvertedAt = DateTime.UtcNow
        };
        return _collection.InsertOneAsync(log);
    }

    public record ConversionLog
    {
        public ObjectId Id { get; set; }
        public string? FileName { get; set; }
        public DateTime ConvertedAt { get; set; }
    }
}
