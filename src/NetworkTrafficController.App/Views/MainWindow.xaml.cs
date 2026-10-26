using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetworkTrafficController.App.ViewModels;

namespace NetworkTrafficController.App.Views;

/// <summary>メインウィンドウ。コードビハインドは表示上の操作（キー入力の扱い）だけを持ち、判断はビューモデルが行う（MVVM）。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// プロセス一覧の選択（複数）をビューモデルに渡す。DataGrid の SelectedItems はバインドできないため、ここで受け渡しだけをする。
    /// </summary>
    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SetSelection(ProcessGrid.SelectedItems.OfType<ProcessRowViewModel>().ToList());
        }
    }

    /// <summary>Esc で選択をすべて外し、全体の表示に戻す。</summary>
    private void ProcessGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ProcessGrid.SelectedItems.Count > 0)
        {
            ProcessGrid.UnselectAll();
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+F で、ウィンドウのどこからでも絞り込み欄へフォーカスを移す。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 絞り込み欄のキー操作。Esc は文字があれば消し（即時に全件へ戻る）、なければ一覧へフォーカスを戻す。Enter / ↓ も一覧へ戻す。
    /// </summary>
    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when FilterBox.Text.Length > 0:
                FilterBox.Clear();
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Enter:
            case Key.Down:
                ProcessGrid.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 数値の入力欄で Enter を押したら、その値を確定してビューモデルに渡す（フォーカスを移さなくても即時反映されるように）。
    /// フォーカスを外したときも確定する（WPF の既定動作）。
    /// </summary>
    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }
}
