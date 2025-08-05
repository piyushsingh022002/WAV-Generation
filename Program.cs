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

    var tempDir = Path.GetTempPath();
    var tempMp3 = Path.Combine(tempDir, Guid.NewGuid() + ".mp3");
    var tempWav = Path.ChangeExtension(tempMp3, ".wav");

    await using (var stream = File.Create(tempMp3))
        await file.CopyToAsync(stream);

    // Convert MP3 to WAV using FFmpeg
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
        return Results.Problem("Audio conversion failed.");

    // Define output directory and expected transcript path
    var outputDir = Path.GetTempPath();
    var transcriptFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(tempWav) + ".txt");

    // Transcribe using Whisper
    var whisper = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "/Users/WorkSpace/whisper-env/bin/python",  // Update this path as needed
            Arguments = $"-m whisper \"{tempWav}\" --language English --model base --output_format txt --output_dir \"{outputDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    whisper.Start();
    string whisperErrors = await whisper.StandardError.ReadToEndAsync();
    await whisper.WaitForExitAsync();

    Console.WriteLine("WHISPER STDERR:");
    Console.WriteLine(whisperErrors);

    if (!File.Exists(transcriptFile))
        return Results.Problem("Whisper transcription failed or produced no output.");

    var transcript = await File.ReadAllTextAsync(transcriptFile);

    await mongoService.SaveConversionAsync(file.FileName);

    // Cleanup
    File.Delete(tempMp3);
    File.Delete(tempWav);
    File.Delete(transcriptFile);

    return Results.Ok(new { transcript });
}).DisableAntiforgery();

app.Run();

public record MongoSettings
{
    public string ConnectionString { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
}

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
