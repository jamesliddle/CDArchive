using System.Windows;
using System.Windows.Threading;

namespace CDArchive.App.Helpers;

public static class DispatcherHelper
{
    public static void RunOnUiThread(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(action);
        }
    }
}
