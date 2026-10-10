using System;
using System.Drawing;
using System.Windows.Forms;

namespace DuoClick
{
    internal sealed class CaptureLimitSettingsDialog : Form
    {
        private readonly NumericUpDown _capacityInput;

        internal CaptureLimitSettingsDialog(AppLanguage language, int capacity)
        {
            CaptureLimitSettings.ValidateCapacity(capacity);

            Text = AppLocalization.Get(language, AppText.CaptureLimitTitle);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(390, 162);

            Label capacityLabel = new Label();
            capacityLabel.AutoSize = true;
            capacityLabel.Anchor = AnchorStyles.Left;
            capacityLabel.Text = AppLocalization.Get(language, AppText.CaptureLimitLabel);
            capacityLabel.Margin = new Padding(0, 3, 8, 3);

            _capacityInput = new NumericUpDown();
            _capacityInput.Minimum = CaptureLimitSettings.MinimumCapacity;
            _capacityInput.Maximum = CaptureLimitSettings.MaximumCapacity;
            _capacityInput.Value = capacity;
            _capacityInput.Width = 82;
            _capacityInput.Anchor = AnchorStyles.Right;
            _capacityInput.Margin = new Padding(8, 0, 0, 0);

            Label explanation = new Label();
            explanation.AutoSize = true;
            explanation.MaximumSize = new Size(350, 0);
            explanation.Text = AppLocalization.Get(language,
                AppText.CaptureLimitExplanation);
            explanation.Margin = new Padding(0, 9, 0, 12);

            Button saveButton = new Button();
            saveButton.Text = AppLocalization.Get(language, AppText.Save);
            saveButton.DialogResult = DialogResult.OK;
            saveButton.MinimumSize = new Size(78, 27);
            saveButton.AutoSize = true;
            saveButton.Margin = new Padding(6, 0, 0, 0);

            Button cancelButton = new Button();
            cancelButton.Text = AppLocalization.Get(language, AppText.Cancel);
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.MinimumSize = new Size(78, 27);
            cancelButton.AutoSize = true;
            cancelButton.Margin = new Padding(6, 0, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Margin = Padding.Empty;
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(cancelButton);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(14, 12, 14, 10);
            layout.ColumnCount = 2;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(capacityLabel, 0, 0);
            layout.Controls.Add(_capacityInput, 1, 0);
            layout.Controls.Add(explanation, 0, 1);
            layout.SetColumnSpan(explanation, 2);
            layout.Controls.Add(buttons, 0, 2);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        internal int Capacity
        {
            get { return Decimal.ToInt32(_capacityInput.Value); }
        }
    }
}
