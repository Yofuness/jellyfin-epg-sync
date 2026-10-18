using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>Re-applies names right after "Refresh Guide", which resets them.</summary>
public class GuideRefreshListener : IHostedService
{
    private readonly ITaskManager _taskManager;

    public GuideRefreshListener(ITaskManager taskManager)
    {
        _taskManager = taskManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        return Task.CompletedTask;
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task.ScheduledTask.Key == "RefreshGuide")
        {
            _taskManager.QueueScheduledTask<RenameChannelsTask>();
        }
    }
}
