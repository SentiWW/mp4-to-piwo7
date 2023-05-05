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

// Calculating how long audio extracvtion will take
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
var totalFrames = (int)Math.Floor(inputFile.Metadata.VideoData.Fps * inputFile.Metadata.Duration.TotalSeconds);
var timeToExtractVideo = TimeSpan.FromSeconds(totalFrames / inputFile.Metadata.VideoData.Fps);

// Export all frames
Console.WriteLine($"Exporting video frames will take approximately {timeToExtractVideo.Seconds} seconds. Do you want to continue (y/n)?");

key = Console.ReadKey();
if (key.Key != ConsoleKey.Y)
    return;

Console.WriteLine($"{Environment.NewLine}Exporting video frames... ");
await Task.Run(() => engine.CustomCommand($"-i {inputVideoPath} -vf \"select=gte(n\\,0)\" -vsync vfr {Path.Join(outputFramesPath, "raw-frame-%d.bmp")}")); 
Console.WriteLine("Video frames exported.");

const int width = 12;
const int height = 10;

var resizeSize = new MagickGeometry(width, height)
{
    IgnoreAspectRatio = true
};

var outputBuilder = new StringBuilder();

Console.WriteLine("Processing video frames, this might take a while...");
    
foreach (var interpolateMethod in Enum.GetValues<PixelInterpolateMethod>())
{
    if (interpolateMethod is PixelInterpolateMethod.Undefined)
        continue;

    var interpolateMethodName = Enum.GetName(interpolateMethod)!.ToLower();
    var piwoOutputPath = Path.Combine(dataDirectory, $"output-{interpolateMethodName}.piwo7");

    if (File.Exists(piwoOutputPath))
        File.Delete(piwoOutputPath);
    
    outputBuilder.AppendLine(piwoHeader);
    
    for (var currentFrameNumber = 1; currentFrameNumber <= totalFrames; currentFrameNumber++)
    {
        // Get raw frame
        var rawFrameFilePath = Path.Combine(outputFramesPath, $"raw-frame-{currentFrameNumber}.bmp");

        // Resize frame and interpolate
        using var frame = new MagickImage(rawFrameFilePath);
        frame.InterpolativeResize(resizeSize, interpolateMethod);
        
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

        Console.WriteLine($"Frames processed ({interpolateMethodName} interpolation): {currentFrameNumber + 1}/{totalFrames}");
        
        ArrayPool<string>.Shared.Return(buffer);
    }
    
    await using var writer = new StreamWriter(piwoOutputPath);
    await writer.WriteAsync(outputBuilder.ToString());
    await writer.FlushAsync();
    writer.Close();
    outputBuilder.Clear();
    
    Console.WriteLine($"Output file created for {interpolateMethodName} interpolation: {piwoOutputPath}");
}

Console.WriteLine("Remove temporary frame files (y/n)?");

key = Console.ReadKey();
if (key.Key != ConsoleKey.Y)
    return;
    
Directory.Delete(outputFramesPath, true);    