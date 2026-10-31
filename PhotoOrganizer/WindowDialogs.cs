namespace PhotoOrganizer;

internal interface IWindowDialogs
{
    bool Confirm(IWin32Window owner, string message, string title);
    void Notify(IWin32Window owner, string message, string title);
}
internal sealed class WindowDialogs : IWindowDialogs
{
    public bool Confirm(IWin32Window owner, string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
    public void Notify(IWin32Window owner, string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
