using Zombies.Engine.Core.Modding;

namespace Zombies.Engine.Ui;

/// <summary>
/// The warning a player sees before installing a Code mod (ADR 0004): its code is trusted, runs with full access to their
/// computer, and is not sandboxed. Installing goes ahead only when they choose <see cref="InstallButton"/>; backing out
/// chooses <see cref="CancelButton"/>. The text is localized from the base mod's string tables.
/// </summary>
public static class CodeModWarning
{
    public const string TitleKey = "dialog.install_code_mod.title";
    public const string MessageKey = "dialog.install_code_mod.message";
    public const string CaptionKey = "dialog.install_code_mod.caption";
    public const string InstallKey = "dialog.install_code_mod.yes";
    public const string CancelKey = "dialog.install_code_mod.no";

    public const string InstallButton = "install";
    public const string CancelButton = "cancel";

    /// <summary>Every key the warning reads, which each language should have.</summary>
    public static IReadOnlyList<string> Keys { get; } = [TitleKey, MessageKey, CaptionKey, InstallKey, CancelKey];

    /// <summary>Whether installing this mod must first show the warning. Only a Code mod does; a Data mod is safe by nature.</summary>
    public static bool IsNeeded(ModInstallReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.NeedsTrust;
    }

    /// <summary>The warning for one mod, naming it. Cancel is focused first, so pressing Enter by habit does not install.</summary>
    /// <exception cref="ArgumentException">The mod is not a Code mod, so it needs no warning.</exception>
    public static Dialog Create(Localizer localizer, ModInstallReview review)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (!IsNeeded(review))
        {
            throw new ArgumentException($"'{review.Manifest.Id}' is not a Code mod, so it installs without a warning.", nameof(review));
        }

        return Dialog.Create(
            localizer,
            TitleKey,
            MessageKey,
            CaptionKey,
            [new DialogButton(CancelButton, CancelKey, IsCancel: true), new DialogButton(InstallButton, InstallKey)],
            review.Manifest.Name);
    }

    /// <summary>Whether the player's choice in the warning lets the install go ahead.</summary>
    public static bool Trusted(Dialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return string.Equals(dialog.Result, InstallButton, StringComparison.Ordinal);
    }
}
