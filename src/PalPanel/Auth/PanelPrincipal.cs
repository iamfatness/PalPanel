namespace PalPanel.Auth;

// Role is always one of: Admin | Viewer | Blocked
public record PanelPrincipal(string Email, string Role);
