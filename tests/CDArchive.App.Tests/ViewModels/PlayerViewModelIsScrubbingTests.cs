using System.ComponentModel;
using CDArchive.App.ViewModels;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// M1 regression: <see cref="PlayerViewModel.IsScrubbing"/> is now an
/// <c>[ObservableProperty]</c>, so changes raise <see cref="INotifyPropertyChanged"/>
/// rather than mutating silently. Pre-fix it was a bare <c>public bool ... { get; private set; }</c>
/// — bindings couldn't observe scrub state and the only way to test it was
/// to call <c>BeginScrub</c> / <c>EndScrub</c> and inspect the property
/// imperatively (no event to wait on).
/// </summary>
public class PlayerViewModelIsScrubbingTests
{
    private static PlayerViewModel Build() => new(
        Substitute.For<IAudioPlayerService>(),
        Substitute.For<IArchiveAudioLocator>(),
        Substitute.For<IArchiveSettings>(),
        Substitute.For<ICanonDataService>());

    [Fact]
    public void BeginScrub_RaisesPropertyChanged_ForIsScrubbing()
    {
        var vm = Build();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.BeginScrub();

        Assert.Contains(nameof(PlayerViewModel.IsScrubbing), raised);
        Assert.True(vm.IsScrubbing);
    }

    [Fact]
    public void EndScrub_RaisesPropertyChanged_AndClearsIsScrubbing()
    {
        var vm = Build();
        vm.BeginScrub();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.EndScrub(seconds: 12.5);

        Assert.Contains(nameof(PlayerViewModel.IsScrubbing), raised);
        Assert.False(vm.IsScrubbing);
    }
}
