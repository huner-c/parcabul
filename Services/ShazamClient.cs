using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ParcaBul.Services;

internal sealed record Track(string Title, string Artist, string? Album, Uri? SongLink, Uri SpotifyLink, Uri? AppleMusicLink, Uri? Cover);

internal sealed class RecognitionException(string message) : Exception(message);

internal sealed class ShazamClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Track?> RecognizeAsync(byte[] wave, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string engine = Path.Combine(AppContext.BaseDirectory, "engine");
        string script = Path.Combine(engine, "recognize.py");
        string python = Path.Combine(engine, "python.exe");
        if (!File.Exists(python) || !File.Exists(script))
            throw new RecognitionException("Recognition files are missing. Keep the engine folder next to ParcaBul.exe.");

        var startInfo = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = engine
        };
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add(script);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        using var process = new Process { StartInfo = startInfo };
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new RecognitionException("Could not start the recognition engine.");

            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.BaseStream.WriteAsync(wave, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(token), output, error).ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new RecognitionException("Could not run the recognition engine. Check the application files.");

            return ParseResponse(await output.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            token.ThrowIfCancellationRequested();
            throw new RecognitionException("Could not run the recognition engine. Check the application files.");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && process.HasExited)
                {
                    // The worker finished while cancellation was stopping it.
                }
            }
        }
    }

    private static Track? ParseResponse(string json)
    {
        EngineResponse response = JsonSerializer.Deserialize<EngineResponse>(json, JsonOptions)
            ?? throw new RecognitionException("The recognition service returned an invalid response.");
        if (response.Error is not null)
            throw new RecognitionException(response.Error == "network"
                ? "Could not connect to Shazam. Check your internet connection and try again."
                : "Could not fingerprint the audio. Try a section where the music is easier to hear.");

        if (response.Track is not { } track)
            return null;

        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            throw new RecognitionException("The recognition service returned incomplete track details.");

        var spotify = new Uri("https://open.spotify.com/search/" + Uri.EscapeDataString($"{track.Title} {track.Artist}"));
        return new Track(track.Title, track.Artist, track.Album, ReadLink(track.Url), spotify, ReadLink(track.AppleMusic), ReadLink(track.Cover));
    }

    private static Uri? ReadLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private sealed record EngineResponse(EngineTrack? Track, string? Error);
    private sealed record EngineTrack(string? Title, string? Artist, string? Album, string? Url, string? AppleMusic, string? Cover);
}
