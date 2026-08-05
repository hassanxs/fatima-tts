using System.Windows;
using System.Windows.Controls;
using FatimaTTS.Models;

namespace FatimaTTS.Views;

/// <summary>
/// Modal dialog for editing a cloned voice's metadata. On save, exposes an
/// <see cref="UpdateVoiceRequest"/> containing only the fields to update.
/// </summary>
public partial class EditVoiceWindow : Window
{
    /// <summary>Populated when the user clicks Save (DialogResult == true).</summary>
    public UpdateVoiceRequest? Result { get; private set; }

    public EditVoiceWindow(InworldVoice voice)
    {
        InitializeComponent();

        NameBox.Text       = voice.DisplayName;
        DescBox.Text       = voice.Description ?? "";
        TagsBox.Text       = string.Join(", ", voice.Tags);
        CategoriesBox.Text = string.Join(", ", voice.Categories);

        PopulateCombo(GenderCombo,
            [("", "(unchanged)"), ("male", "Male"), ("female", "Female"), ("neutral", "Neutral")],
            voice.Gender);
        PopulateCombo(AgeCombo,
            [("", "(unchanged)"), ("young", "Young"), ("middle_aged", "Middle-aged"), ("elderly", "Elderly")],
            voice.AgeGroup);
    }

    private static void PopulateCombo(ComboBox combo, (string Tag, string Label)[] items, string? current)
    {
        combo.Items.Clear();
        foreach (var (tag, label) in items)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

        current ??= "";
        foreach (ComboBoxItem item in combo.Items)
            if ((item.Tag as string ?? "") == current) { combo.SelectedItem = item; return; }
        combo.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static List<string> SplitCsv(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("Display name cannot be empty.", "Edit Voice",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var req = new UpdateVoiceRequest
        {
            DisplayName = NameBox.Text.Trim(),
            Description = DescBox.Text.Trim(),
            Tags        = SplitCsv(TagsBox.Text),
        };

        var gender = ComboTag(GenderCombo);
        if (gender.Length > 0) req.Gender = gender;

        var age = ComboTag(AgeCombo);
        if (age.Length > 0) req.AgeGroup = age;

        // Only send categories when the user actually provided some (an invalid value 400s).
        var cats = SplitCsv(CategoriesBox.Text);
        if (cats.Count > 0) req.Categories = cats;

        Result       = req;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
