using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The version tags on script and style links (asp-append-version) watch each file they hash. A real launcher is one host
/// with one watcher, but the tests run hundreds of hosts at once, and on Linux each watcher is an inotify instance, capped
/// at 128 per user. So a test host's web root serves files as usual but never watches them: a test doesn't edit them.
/// </summary>
internal sealed class NoWebRootWatchers : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        env.WebRootFileProvider = new Unwatched(env.WebRootFileProvider);
        next(app);
    };

    internal sealed class Unwatched(IFileProvider inner) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);
        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
