using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public sealed class QuickSearchKeyboardTests : WpfTestBase
{
    [WpfFact]
    public async Task EnterConsumesInputBeforeAsyncSearchAndCapturesControlScope()
    {
        foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control,ModifierKeys.Control|ModifierKeys.Shift})
        {
            var e=new KeyEventArgs(Keyboard.PrimaryDevice,new StubSource(),0,Key.Enter)
                {RoutedEvent=Keyboard.PreviewKeyDownEvent};
            var pending=new TaskCompletionSource();bool? scope=null;
            var operation=QuickSearchKeyboard.ExecuteEnterAsync(e,modifiers,everywhere=>
            {
                Assert.True(e.Handled);
                scope=everywhere;
                return pending.Task;
            });
            Assert.True(e.Handled);
            Assert.False(operation.IsCompleted);
            Assert.Equal((modifiers&ModifierKeys.Control)!=0,scope);
            pending.SetResult();await operation;
        }
    }
    private sealed class StubSource : PresentationSource
    {
        public override Visual RootVisual {get;set;}=new System.Windows.Shapes.Rectangle();
        public override bool IsDisposed=>false;
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
    }
}
