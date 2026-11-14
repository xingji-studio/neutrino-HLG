using System.Drawing;

static class SecureLoginPolicy
{
    // Only accept an account label immediately above the focused password
    // field; account names in the separate user-switcher must not qualify.
    public static bool IsNearby(RectangleF field, RectangleF label) {
        if (field.Width <= 0 || field.Height <= 0 || label.Width <= 0 || label.Height <= 0) return false;
        var fieldCenter = field.Left + field.Width / 2;
        var labelCenter = label.Left + label.Width / 2;
        var horizontalDistance = Math.Abs(fieldCenter - labelCenter);
        var verticalDistance = field.Top - label.Bottom;
        return horizontalDistance <= Math.Max(80, field.Width * .75f)
            && verticalDistance >= -field.Height * .25f
            && verticalDistance <= Math.Max(160, field.Height * 5);
    }
}
