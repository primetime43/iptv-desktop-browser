using System.Windows.Controls;
using System.Windows.Input;
using DesktopApp.ViewModels;

namespace DesktopApp.Views.Dashboard;

public partial class RecordingFormView : UserControl
{
    public RecordingFormView() => InitializeComponent();

    // Pointer hover is view behavior; all form state and scheduling actions live in the model.
    private void ProgramComboItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is RecordingFormViewModel model && sender is ComboBoxItem { Content: RecordingProgram program })
            model.HoveredProgram = program;
    }

    private void ProgramComboItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is RecordingFormViewModel model) model.HoveredProgram = null;
    }
}
