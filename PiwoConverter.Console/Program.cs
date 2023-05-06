using System.Buffers;
using System.Text;

using CommandLine;

using ImageMagick;

using MediaToolkit;
using MediaToolkit.Model;

using PiwoConverter.Console;

var paramsResult = Parser.Default.ParseArguments<Options>(args);

if (paramsResult.Errors.Any())
    return;

var currentDirectory = Directory.GetCurrentDirectory();
var dataDirectory = Path.Combine(currentDirectory, "data");
var inputVideoPath = paramsResult.Value.InputPath;
var outputAudioPath = Path.Combine(dataDirectory, "audio.mp3");
var outputFramesPath = Path.Combine(dataDirectory, "frames");
const int desiredFramerate = 20;
var outputVideoPath = Path.Combine(dataDirectory, $"video-{desiredFramerate}-fps.mp4");

// https://ffmpeg.org/ffmpeg-scaler.html#toc-Scaler-Options
var scalingAlgorithms = new []
{
    "fast_bilinear", 
    "bilinear", 
    "bicubic", 
    "experimental", 
    "neighbor", 
    "area", 
    "bicublin", 
    "gauss", 
    "sinc", 
    "lanczos", 
    "spline"
};

var piwoHeader = $"PIWO_7_FILE{Environment.NewLine}" +
                      $"12 10{Environment.NewLine}";

if (!Directory.Exists(dataDirectory))
    Directory.CreateDirectory(dataDirectory);

if (!Directory.Exists(outputFramesPath))
    Directory.CreateDirectory(outputFramesPath);

using var engine = new Engine();
var inputFile = new MediaFile
{
    Filename = inputVideoPath
};

engine.GetMetadata(inputFile);

// Change the framerate of the video to 20fps
if (Math.Abs(inputFile.Metadata.VideoData.Fps - desiredFramerate) > 0.1)
{
    engine.CustomCommand($"-i {inputVideoPath} -r {desiredFramerate} -c:v copy -c:a copy {outputVideoPath}");
    inputVideoPath = outputVideoPath;
    inputFile.Filename = outputVideoPath;
    engine.GetMetadata(inputFile);
}
// Calculating how long audio extraction will take
var audioSize = inputFile.Metadata.Duration.TotalSeconds * inputFile.Metadata.AudioData.BitRateKbs / 8;
var timeToExtractAudio = TimeSpan.FromSeconds(audioSize / inputFile.Metadata.AudioData.BitRateKbs);

// Extract audio from mp4
Console.WriteLine($"Exporting audio will take approximately {timeToExtractAudio.Seconds} seconds. Do you want to continue (y/n)?");

var key = Console.ReadKey();
if (key.Key != ConsoleKey.Y)
    return;

Console.WriteLine($"{Environment.NewLine}Exporting audio...");
await Task.Run(() => engine.CustomCommand($"-i {inputVideoPath} -vn -acodec libmp3lame -qscale:a 2 {outputAudioPath}"));
Console.WriteLine($"Audio exported to {outputAudioPath}.");

// Calculating how long extraction will take
var totalFrames = (int)Math.Floor(desiredFramerate * inputFile.Metadata.Duration.TotalSeconds);
var timeToExtractVideo = TimeSpan.FromSeconds(totalFrames / desiredFramerate);

// Export all frames
Console.WriteLine($"Exporting video frames will take approximately {timeToExtractVideo.Seconds} seconds. " +
                  $"They need to be exported for each scaling algorithm defined so in total this will take {timeToExtractVideo.Seconds * scalingAlgorithms.Length} seconds. " +
                  $"Do you want to continue (y/n)?");

key = Console.ReadKey();
if (key.Key != ConsoleKey.Y)
    return;

const int width = 12;
const int height = 10;

var outputBuilder = new StringBuilder();

Console.WriteLine($"{Environment.NewLine}Processing video frames, this might take a while...");

foreach (var scalingAlgorithm in scalingAlgorithms)
{
    Console.WriteLine($"Exporting video frames for {scalingAlgorithm} scaling algorithm...");
    await Task.Run(() => engine.CustomCommand($"-i {inputVideoPath} -vf \"select=gte(n\\,0),scale={width}:{height}:sws_flags={scalingAlgorithm}\" -vsync vfr {Path.Join(outputFramesPath, "raw-frame-%d.bmp")}"));
    
    var piwoOutputPath = Path.Combine(dataDirectory, $"output-{scalingAlgorithm}.piwo7");

    if (File.Exists(piwoOutputPath))
        File.Delete(piwoOutputPath);
    
    outputBuilder.AppendLine(piwoHeader);
    
    for (var currentFrameNumber = 1; currentFrameNumber <= totalFrames; currentFrameNumber++)
    {
        // Get raw frame
        var rawFrameFilePath = Path.Combine(outputFramesPath, $"raw-frame-{currentFrameNumber}.bmp");

        // Resize frame and interpolate
        using var frame = new MagickImage(rawFrameFilePath);
        
        // Convert frame to piwo7
        var buffer = ArrayPool<string>.Shared.Rent(width * height);
        var pixels = frame.GetPixels();
        foreach (var pixel in pixels)
        {
            var color = pixel.ToColor();

            if (color is null)
                throw new NullReferenceException("Color was null.");
            
            var red = (byte)color.R;
            var green = (byte)color.G;
            var blue = (byte)color.B;
            var union = (red << 16) | (green << 8) | blue;
            buffer[pixel.Y * width + pixel.X] = union.ToString();
        }
    
        // Append piwo7 frame to file
        outputBuilder.AppendLine("50");
        for (var bufferColumn = 0; bufferColumn < height; bufferColumn++)
        {
            for (var bufferRow = 0; bufferRow < width; bufferRow++)
            {
                outputBuilder.Append($"{buffer[bufferColumn * width + bufferRow]} ");
            }
            outputBuilder.AppendLine();
        }
        outputBuilder.AppendLine();

        Console.WriteLine($"Frames processed ({scalingAlgorithm} scaling): {currentFrameNumber + 1}/{totalFrames}");
        
        ArrayPool<string>.Shared.Return(buffer);
    }
    
    // Write piwo7 file
    await using var writer = new StreamWriter(piwoOutputPath);
    await writer.WriteAsync(outputBuilder.ToString());
    await writer.FlushAsync();
    writer.Close();
    outputBuilder.Clear();
    
    Console.WriteLine($"Output file created for {scalingAlgorithm} scaling: {piwoOutputPath}");
}

Console.WriteLine("Remove temporary files (y/n)?");

key = Console.ReadKey();
if (key.Key != ConsoleKey.Y)
    return;
    
if (Directory.Exists(outputFramesPath))
    Directory.Delete(outputFramesPath, true);    

if (File.Exists(outputVideoPath))
    File.Delete(outputVideoPath);