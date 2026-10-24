using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class UserAccountsForm
{
    private readonly IDbContextFactory<AppDbContext> _accountsFactory;
    private readonly SessionState _accountsSession;
    private async Task ManageSelectedAsync(string action)
    {
        if (_grid.CurrentRow?.Cells["Id"].Value is not int id) { MessageBox.Show(this, "Select a user first."); return; }
        try
        {
            using var db = _accountsFactory.CreateDbContext();
            await AccountAdministration.RequireAdminAsync(db, _accountsSession.UserId);
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
            var first = user.FirstName; var last = user.LastName; var email = user.Email; var role = user.Role;
            int[]? allowed = null; var password = "";
            if (action is "Edit" or "Reset Password")
            {
                using var dialog = new Form { Text = action + " — " + user.Username, ClientSize = new Size(570, 570), AutoScaleMode = AutoScaleMode.Dpi };
                WinTheme.Apply(dialog);
                var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(15) };
                Control Field(string label, Control control) { fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }); control.Width = 500; fields.Controls.Add(control); return control; }
                var firstBox = new TextBox { Text = first, MaxLength = 80 }; var lastBox = new TextBox { Text = last, MaxLength = 80 };
                var emailBox = new TextBox { Text = email, MaxLength = 200 }; var roleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                roleBox.Items.AddRange(Enum.GetValues<UserRole>().Cast<object>().ToArray()); roleBox.SelectedItem = role;
                var all = new CheckBox { Text = "Allow all configured stores", Checked = user.AllowedStoreIds.Length == 0, AutoSize = true };
                var stores = await db.Stores.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
                var storeList = new CheckedListBox { Height = 160, DisplayMember = nameof(Store.Name), CheckOnClick = true, Enabled = !all.Checked };
                foreach (var store in stores) storeList.Items.Add(store, user.AllowsStore(store.Id));
                all.CheckedChanged += (_, _) => storeList.Enabled = !all.Checked;
                var passwordBox = new TextBox { UseSystemPasswordChar = true };
                if (action == "Edit")
                { Field("First name", firstBox); Field("Last name", lastBox); Field("Email", emailBox); Field("Role", roleBox); fields.Controls.Add(all); Field("Permitted stores", storeList); }
                else Field("New password (at least 8 characters)", passwordBox);
                var save = WinTheme.Button("Save", true); save.Dock = DockStyle.Bottom; save.Height = 48; save.DialogResult = DialogResult.OK;
                dialog.Controls.Add(fields); dialog.Controls.Add(save); dialog.AcceptButton = save;
                ThemePreferences.ApplyTree(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                first = firstBox.Text; last = lastBox.Text; email = emailBox.Text; role = (UserRole)roleBox.SelectedItem!;
                allowed = all.Checked ? null : storeList.CheckedItems.Cast<Store>().Select(x => x.Id).ToArray(); password = passwordBox.Text;
            }
            else if (MessageBox.Show(this, $"{action} {user.Username}?" + (action == "Delete" ? "\nOnly unused accounts can be deleted; accounts with history must be deactivated." : ""), "User Accounts", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await AccountAdministration.ChangeAsync(db, _accountsSession.UserId, id, action, first, last, email, role, allowed, password);
            RefreshGrid();
        }
        catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message), "User Accounts", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
