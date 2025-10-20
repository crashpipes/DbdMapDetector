using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tesseract;

// Extension methods pour les graphiques modernes
public static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int cornerRadius)
    {
        if (graphics == null)
            throw new ArgumentNullException(nameof(graphics));
        if (pen == null)
            throw new ArgumentNullException(nameof(pen));

        using (GraphicsPath path = RoundedRect(bounds, cornerRadius))
        {
            graphics.DrawPath(pen, path);
        }
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int cornerRadius)
    {
        if (graphics == null)
            throw new ArgumentNullException(nameof(graphics));
        if (brush == null)
            throw new ArgumentNullException(nameof(brush));

        using (GraphicsPath path = RoundedRect(bounds, cornerRadius))
        {
            graphics.FillPath(brush, path);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        Size size = new Size(diameter, diameter);
        Rectangle arc = new Rectangle(bounds.Location, size);
        GraphicsPath path = new GraphicsPath();

        if (radius == 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        // Top left arc  
        path.AddArc(arc, 180, 90);

        // Top right arc  
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        // Bottom right arc  
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // Bottom left arc 
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}

namespace DbdMapDetector
{
    public partial class Form1 : Form
    {
        private string tessdataPath = "tessdata"; // Dossier avec eng.traineddata
        private RichTextBox debugBox; // Zone pour afficher le texte OCR et messages
        private TrackBar opacitySlider; // Slider pour l'opacité
        private TrackBar sizeSlider; // Slider pour la taille
        private Label opacityLabel; // Label pour l'opacité
        private Label sizeLabel; // Label pour la taille
        private Panel controlPanel; // Panel pour les contrôles
        private Panel debugPanel; // Panel pour le debug
        private RadioButton radioTopLeft; // Radio pour position haut gauche
        private RadioButton radioTopCenter; // Radio pour position haut centre
        private RadioButton radioTopRight; // Radio pour position haut droite
        private GroupBox positionGroupBox; // Groupe pour les boutons radio

        // Référence à l'overlay de la map pour pouvoir le fermer manuellement
        private Form currentMapOverlay;
        private PictureBox currentMapPictureBox;
        private List<string> currentVariants = new List<string>();
        private int currentVariantIndex = -1;
        private float defaultOverlayOpacity = 0.5f;
        private Size defaultOverlaySize = new Size(420, 380);
        private string currentPosition = "top-left"; // Position actuelle de l'overlay

        // Hotkey IDs
        private const int HOTKEY_ID_F1 = 1;
        private const int HOTKEY_ID_F2 = 2;
        private const int HOTKEY_ID_F3 = 3;
        private const uint MOD_NONE = 0x0000;
        private const int WM_HOTKEY = 0x0312;

        // Constants pour rendre l'overlay click-through
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int LWA_ALPHA = 0x2;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        private void Form1_Load(object sender, EventArgs e)
        {
            // Register global hotkeys
            try
            {
                bool r1 = RegisterHotKey(this.Handle, HOTKEY_ID_F1, MOD_NONE, (uint)Keys.F1);
                bool r2 = RegisterHotKey(this.Handle, HOTKEY_ID_F2, MOD_NONE, (uint)Keys.F2);
                bool r3 = RegisterHotKey(this.Handle, HOTKEY_ID_F3, MOD_NONE, (uint)Keys.F3);
            }
            catch (Exception ex)
            {
                AppendDebug("Hotkey registration error: " + ex.Message + "\r\n");
            }

            // Ensure we unregister on closing
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                UnregisterHotKey(this.Handle, HOTKEY_ID_F1);
                UnregisterHotKey(this.Handle, HOTKEY_ID_F2);
                UnregisterHotKey(this.Handle, HOTKEY_ID_F3);
            }
            catch { }
        }

        public Form1()
        {
            InitializeComponent();

            // --- Interface visible with dark theme ---
            this.Text = "DBD Map Detector";
            this.Size = new Size(500, 420); // Taille de fenêtre ajustée pour accommoder le panneau de contrôle
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30); // Fond sombre
            this.ForeColor = Color.White; // Texte en blanc

            // Création du layout avec deux panneaux (debug en haut, contrôles en bas) - style dark theme
            debugPanel = new Panel();
            debugPanel.Dock = DockStyle.Fill;
            debugPanel.BackColor = Color.FromArgb(40, 40, 40); // Légèrement plus clair que le fond
            debugPanel.Padding = new Padding(10);
            this.Controls.Add(debugPanel);
            
            controlPanel = new Panel();
            controlPanel.Dock = DockStyle.Bottom;
            controlPanel.Height = 140; // Hauteur augmentée davantage pour assurer l'affichage complet
            controlPanel.Padding = new Padding(10);
            controlPanel.BackColor = Color.FromArgb(50, 50, 50); // Panel de contrôle un peu plus clair
            controlPanel.Paint += (s, e) => {
                // Ajouter une bordure subtile en haut du panel
                e.Graphics.DrawLine(new Pen(Color.FromArgb(70, 70, 70), 1), 
                    0, 0, controlPanel.Width, 0);
            };
            this.Controls.Add(controlPanel);

            // NOUVELLE DISPOSITION : Position radio buttons à gauche, sliders à droite
            
            // Groupe pour les boutons radio (à gauche) - style modern dark
            positionGroupBox = new GroupBox();
            positionGroupBox.Text = "Position";
            positionGroupBox.Location = new Point(10, 15);
            positionGroupBox.Size = new Size(150, 110); // Légèrement plus grand
            positionGroupBox.ForeColor = Color.FromArgb(200, 200, 200);
            positionGroupBox.BackColor = Color.FromArgb(60, 60, 60);
            positionGroupBox.FlatStyle = FlatStyle.Flat;
            positionGroupBox.Paint += (s, e) => {
                // Bordure personnalisée avec coins arrondis
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(100, 100, 100), 1))
                {
                    var rect = new Rectangle(0, 0, positionGroupBox.Width - 1, positionGroupBox.Height - 1);
                    e.Graphics.DrawRoundedRectangle(pen, rect, 6);
                }
            };
            controlPanel.Controls.Add(positionGroupBox);

            // Radio boutons pour les positions - style modern
            radioTopLeft = new RadioButton();
            radioTopLeft.Text = "Top Left";
            radioTopLeft.Location = new Point(10, 20);
            radioTopLeft.Checked = true;
            radioTopLeft.ForeColor = Color.White;
            radioTopLeft.BackColor = Color.Transparent;
            radioTopLeft.CheckedChanged += RadioPosition_CheckedChanged;
            positionGroupBox.Controls.Add(radioTopLeft);

            radioTopCenter = new RadioButton();
            radioTopCenter.Text = "Top Center";
            radioTopCenter.Location = new Point(10, 45);
            radioTopCenter.ForeColor = Color.White;
            radioTopCenter.BackColor = Color.Transparent;
            radioTopCenter.CheckedChanged += RadioPosition_CheckedChanged;
            positionGroupBox.Controls.Add(radioTopCenter);

            radioTopRight = new RadioButton();
            radioTopRight.Text = "Top Right";
            radioTopRight.Location = new Point(10, 70);
            radioTopRight.ForeColor = Color.White;
            radioTopRight.BackColor = Color.Transparent;
            radioTopRight.CheckedChanged += RadioPosition_CheckedChanged;
            positionGroupBox.Controls.Add(radioTopRight);

            // Sliders à droite - Nouvelle disposition avec plus d'espace entre les contrôles
            
            // Création des labels et sliders d'opacité (en haut) - style modern dark
            opacityLabel = new Label();
            opacityLabel.Text = "Opacity: 50%";
            opacityLabel.AutoSize = false; // Force taille fixe
            opacityLabel.TextAlign = ContentAlignment.MiddleLeft;
            opacityLabel.Width = 120;
            opacityLabel.Height = 20;
            opacityLabel.Location = new Point(170, 15);
            opacityLabel.ForeColor = Color.FromArgb(220, 220, 220);
            opacityLabel.Font = new Font(opacityLabel.Font.FontFamily, opacityLabel.Font.Size, FontStyle.Bold);
            controlPanel.Controls.Add(opacityLabel);

            opacitySlider = new TrackBar();
            opacitySlider.Minimum = 10; // 10% minimum
            opacitySlider.Maximum = 100; // 100% maximum
            opacitySlider.Value = 50; // 50% par défaut
            opacitySlider.Width = 260;
            opacitySlider.Location = new Point(200, 35);
            opacitySlider.TickFrequency = 10;
            opacitySlider.BackColor = Color.FromArgb(60, 60, 60);
            opacitySlider.ValueChanged += OpacitySlider_ValueChanged;
            controlPanel.Controls.Add(opacitySlider);

            // Création des labels et sliders de taille (en bas avec plus d'espace) - style modern dark
            sizeLabel = new Label();
            sizeLabel.Text = "Size: 80%";
            sizeLabel.AutoSize = false; // Force taille fixe
            sizeLabel.TextAlign = ContentAlignment.MiddleLeft;
            sizeLabel.Width = 120;
            sizeLabel.Height = 20;
            sizeLabel.Location = new Point(170, 75); // Plus bas pour éviter tout chevauchement
            sizeLabel.ForeColor = Color.FromArgb(220, 220, 220);
            sizeLabel.Font = new Font(sizeLabel.Font.FontFamily, sizeLabel.Font.Size, FontStyle.Bold);
            controlPanel.Controls.Add(sizeLabel);

            sizeSlider = new TrackBar();
            sizeSlider.Minimum = 50; // 50% minimum
            sizeSlider.Maximum = 200; // 200% maximum
            sizeSlider.Value = 80; // 80% par défaut
            sizeSlider.Width = 260;
            sizeSlider.Location = new Point(200, 95); // Plus bas pour assurer la séparation
            sizeSlider.TickFrequency = 10;
            sizeSlider.BackColor = Color.FromArgb(60, 60, 60);
            sizeSlider.ValueChanged += SizeSlider_ValueChanged;
            controlPanel.Controls.Add(sizeSlider);

            // Création de la zone de debug - style modern dark
            // Utilisation de RichTextBox pour support de la coloration
            debugBox = new RichTextBox();
            debugBox.Multiline = true;
            debugBox.Dock = DockStyle.Fill;
            debugBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            debugBox.ReadOnly = true;
            debugBox.BackColor = Color.FromArgb(30, 30, 30);
            debugBox.ForeColor = Color.FromArgb(0, 255, 127); // Vert néon pour le texte de debug
            debugBox.BorderStyle = BorderStyle.None;
            debugBox.Font = new Font("Consolas", 9F, FontStyle.Regular);
            debugBox.Margin = new Padding(5);
            debugPanel.Controls.Add(debugBox);
            
            // Activer la capture des touches au niveau du formulaire
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;
            
            // Ajout d'un effet de bordure moderne au bas de la fenêtre
            this.Paint += (s, e) => 
            {
                // Dessine une ligne de couleur primaire au bas de la fenêtre
                using (var accentBrush = new LinearGradientBrush(
                    new Point(0, this.Height - 2), 
                    new Point(this.Width, this.Height - 2), 
                    Color.FromArgb(0, 100, 255), 
                    Color.FromArgb(0, 255, 127)))
                {
                    e.Graphics.FillRectangle(accentBrush, new Rectangle(0, this.Height - 2, this.Width, 2));
                }
            };

            // Startup banner and instructions
            AppendDebug("┌─────────────────────────────────────────────────────────────────────┐\r\n");
            AppendDebug("│ ✧ Welcome To DBD Map Detector                                                                        ✧ │\r\n");
            AppendDebug("└─────────────────────────────────────────────────────────────────────┘\r\n");
            AppendDebug("\r\n • To use in-game: press ESC to open the menu, then press F3 to capture the map\r\n");
            AppendDebug("\r\n • [F3] Start map detection\r\n");
            AppendDebug(" • [F1] Cycle image variant\r\n");
            AppendDebug(" • [F2] Close current overlay\r\n");
            AppendDebug("\r\n • Use the sliders to adjust opacity and size\r\n");
            AppendDebug(" • Use the radio buttons to change overlay position\r\n");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID_F3)
                {
                    AppendDebug("Global hotkey F3 received: starting scan...\r\n");
                    string mapName = DetectMapName();
                    if (!string.IsNullOrEmpty(mapName))
                        ShowMapOverlay(mapName);
                    else
                        AppendDebug("No text detected.\r\n");
                }
                else if (id == HOTKEY_ID_F2)
                {
                    AppendDebug("Global hotkey F2 received: closing overlay...\r\n");
                    if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
                    {
                        try { currentMapOverlay.Close(); } catch (Exception ex) { AppendDebug("Error closing overlay: " + ex.Message + "\r\n"); }
                        currentMapOverlay = null;
                        currentVariants.Clear();
                        currentVariantIndex = -1;
                    }
                }
                else if (id == HOTKEY_ID_F1)
                {
                    AppendDebug("Global hotkey F1 received: cycling variant...\r\n");
                    if (currentVariants != null && currentVariants.Count > 1 && currentMapPictureBox != null)
                    {
                        currentVariantIndex = (currentVariantIndex + 1) % currentVariants.Count;
                        SetCurrentMapImage(currentVariants[currentVariantIndex]);
                        AppendDebug($"Variant {currentVariantIndex + 1}/{currentVariants.Count} displayed.\r\n");
                    }
                }
            }

            base.WndProc(ref m);
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F3)
            {
                AppendDebug("F3 pressed: starting scan...\r\n");
                string mapName = DetectMapName();

                if (!string.IsNullOrEmpty(mapName))
                {
                    ShowMapOverlay(mapName);
                }
                else
                {
                    AppendDebug("No text detected.\r\n");
                }
            }
            else if (e.KeyCode == Keys.F2)
            {
                // Fermer l'overlay s'il est affiché
                if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
                {
                    try
                    {
                        currentMapOverlay.Close();
                    }
                    catch (Exception ex)
                    {
                        AppendDebug("Error closing overlay: " + ex.Message + "\r\n");
                    }
                    finally
                    {
                        currentMapOverlay = null;
                        currentVariants.Clear();
                        currentVariantIndex = -1;
                        AppendDebug("Map overlay closed (F2).\r\n");
                    }
                }
            }
            else if (e.KeyCode == Keys.F1)
            {
                // Cycle variants
                if (currentVariants != null && currentVariants.Count > 1 && currentMapPictureBox != null)
                {
                    currentVariantIndex = (currentVariantIndex + 1) % currentVariants.Count;
                    SetCurrentMapImage(currentVariants[currentVariantIndex]);
                    AppendDebug($"Variant {currentVariantIndex + 1}/{currentVariants.Count} displayed.\r\n");
                }
            }
        }

        private string DetectMapName()
        {
            try
            {
                // Zone à ajuster selon ta résolution (ici : en bas à gauche)
                Rectangle captureZone = new Rectangle(700, 885, 580, 27);

                // Affiche brièvement la zone qui sera scannée
                ShowCaptureZoneOverlay(captureZone);

                Bitmap screenshot = new Bitmap(captureZone.Width, captureZone.Height);

                using (Graphics g = Graphics.FromImage(screenshot))
                {
                    g.CopyFromScreen(captureZone.Location, Point.Empty, captureZone.Size);
                }

                // Utilise plusieurs stratégies de prétraitement et choisit le meilleur résultat
                string result = null;
                try
                {
                    result = TryBestOcr(screenshot);
                    AppendDebug("Final OCR result: " + (result ?? "(empty)") + "\r\n");
                }
                catch (Exception ex)
                {
                    AppendDebug("Multi-strategy OCR error: " + ex.Message + "\r\n");
                }

                // Nettoyage
                try { screenshot.Dispose(); } catch { }

                return result;
            }
            catch (Exception ex)
            {
                AppendDebug("OCR error: " + ex.Message + "\r\n");
            }

            return null;
        }

        // Enhance image: grayscale, increase contrast, then threshold
        private Bitmap EnhanceForOcr(Bitmap src)
        {
            int w = src.Width;
            int h = src.Height;

            // 1) Convert to grayscale using ColorMatrix (fast)
            Bitmap gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(gray))
            {
                var cm = new ColorMatrix(new float[][]
                {
                    new float[] {0.299f, 0.299f, 0.299f, 0, 0},
                    new float[] {0.587f, 0.587f, 0.587f, 0, 0},
                    new float[] {0.114f, 0.114f, 0.114f, 0, 0},
                    new float[] {0,      0,      0,      1, 0},
                    new float[] {0,      0,      0,      0, 1}
                });

                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }
            }

            // 2) Increase contrast using a simple pixel transform (suitable for small areas)
            Bitmap contrastBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            // contrast value between -100 and 100
            float contrast = 40f;
            float factor = (259 * (contrast + 255)) / (255 * (259 - contrast));

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = gray.GetPixel(x, y);
                    int v = c.R; // gray so R=G=B
                    int nv = Truncate((int)(factor * (v - 128) + 128));
                    Color nc = Color.FromArgb(nv, nv, nv);
                    contrastBmp.SetPixel(x, y, nc);
                }
            }

            // 3) Simple adaptive-ish threshold: compute average and threshold
            // compute mean
            long sum = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    sum += contrastBmp.GetPixel(x, y).R;
            int mean = (int)(sum / (w * h));
            int threshold = Math.Max(100, mean - 10); // ensure not too low

            // create 24bpp temp to draw threshold result then return
            Bitmap thresh = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = contrastBmp.GetPixel(x, y).R;
                    Color pixel = (v < threshold) ? Color.Black : Color.White;
                    thresh.SetPixel(x, y, pixel);
                }
            }

            // dispose intermediates
            gray.Dispose();
            contrastBmp.Dispose();

            return thresh;
        }

        private int Truncate(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        // Affiche une superposition temporaire indiquant la zone de capture
        private void ShowCaptureZoneOverlay(Rectangle captureZone)
        {
            try
            {
                Form overlay = new Form();
                overlay.FormBorderStyle = FormBorderStyle.None;
                overlay.StartPosition = FormStartPosition.Manual;
                overlay.Bounds = Screen.PrimaryScreen.Bounds;
                overlay.TopMost = true;
                overlay.ShowInTaskbar = false;
                // Légère obscurcissement de l'écran pour mieux voir la zone
                overlay.BackColor = Color.Black;
                overlay.Opacity = 0.35; // Plus foncé pour meilleur contraste avec le contour lumineux

                overlay.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    // Rectangle principal avec effet glow
                    for (int i = 5; i > 0; i--)
                    {
                        using (Pen glowPen = new Pen(Color.FromArgb(20 * (6 - i), 0, 255, 127), i))
                        {
                            Rectangle rect = new Rectangle(
                                captureZone.X - i, captureZone.Y - i,
                                captureZone.Width + i * 2, captureZone.Height + i * 2);
                            e.Graphics.DrawRoundedRectangle(glowPen, rect, 3);
                        }
                    }
                    // Rectangle principal
                    using (Pen p = new Pen(Color.FromArgb(200, 0, 255, 127), 2))
                    {
                        e.Graphics.DrawRoundedRectangle(p, captureZone, 3);
                    }
                    
                    // Légende
                    using (Brush textBrush = new SolidBrush(Color.FromArgb(230, 0, 255, 127)))
                    using (Font f = new Font("Segoe UI", 9, FontStyle.Bold))
                    {
                        e.Graphics.DrawString("Capture area", f, textBrush, 
                            captureZone.X, captureZone.Y - 20);
                    }
                };

                overlay.Show();
                
                // Rendre la fenêtre click-through
                MakeClickThrough(overlay.Handle);
                
                overlay.Invalidate();

                Timer t = new Timer();
                t.Interval = 800; // ms
                t.Tick += (s, e) =>
                {
                    try { overlay.Close(); } catch { }
                    try { t.Stop(); } catch { }
                };
                t.Start();
            }
            catch (Exception ex)
            {
                AppendDebug("Overlay error: " + ex.Message + "\r\n");
            }
        }
        
        // Rend une fenêtre click-through (les clics passent à travers)
        private void MakeClickThrough(IntPtr handle)
        {
            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        private void ShowMapOverlay(string mapName)
        {
            // Nettoyage du nom pour éviter les caractères interdits
            string sanitized = new string(mapName
                .Where(c => char.IsLetterOrDigit(c))
                .ToArray())
                .ToLower();

            AppendDebug("Sanitized name: " + sanitized + "\r\n");

            // Reset variants
            currentVariants.Clear();
            currentVariantIndex = -1;

            string singleImagePath = Path.Combine("Maps", sanitized + ".jpg");

            if (File.Exists(singleImagePath))
            {
                currentVariants.Add(singleImagePath);
            }
            else
            {
                string dirPath = Path.Combine("Maps", sanitized);
                if (Directory.Exists(dirPath))
                {
                    var imgs = Directory.GetFiles(dirPath)
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f)
                        .ToList();

                    if (imgs.Count > 0)
                        currentVariants.AddRange(imgs);
                }
            }

            if (currentVariants.Count == 0)
            {
                AppendDebug("❌ Image not found for: " + sanitized + "\r\n");
                return;
            }

            AppendDebug("✅ Image(s) found: " + currentVariants.Count + " — showing overlay...\r\n");

            // Fermer l'overlay précédent s'il existe
            if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
            {
                try { currentMapOverlay.Close(); } catch { }
                currentMapOverlay = null;
            }

            // Affichage overlay (fenêtre image simple) - style moderne avec bordure
            currentMapOverlay = new Form();
            currentMapOverlay.FormBorderStyle = FormBorderStyle.None;
            currentMapOverlay.TopMost = true;
            currentMapOverlay.StartPosition = FormStartPosition.Manual;
            
            // Utilise les valeurs des sliders
            int percentage = sizeSlider.Value;
            int width = (int)(defaultOverlaySize.Width * percentage / 100);
            int height = (int)(defaultOverlaySize.Height * percentage / 100);
            currentMapOverlay.Size = new Size(width, height);
            
            // Détermine la position selon le choix de l'utilisateur
            UpdateOverlayPosition(false);
            
            currentMapOverlay.BackColor = Color.FromArgb(20, 20, 20);
            currentMapOverlay.Opacity = defaultOverlayOpacity;
            currentMapOverlay.ShowInTaskbar = false;
            
            // Ajout d'une bordure moderne
            currentMapOverlay.Paint += (s, e) => {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, currentMapOverlay.Width - 1, currentMapOverlay.Height - 1);
                // Bordure élégante en couleur primaire
                using (var pen = new Pen(Color.FromArgb(100, 0, 255, 127), 2))
                {
                    e.Graphics.DrawRoundedRectangle(pen, rect, 8);
                }
            };

            currentMapPictureBox = new PictureBox();
            currentMapPictureBox.Dock = DockStyle.Fill;
            currentMapPictureBox.SizeMode = PictureBoxSizeMode.StretchImage;
            currentMapOverlay.Controls.Add(currentMapPictureBox);

            currentVariantIndex = 0;
            SetCurrentMapImage(currentVariants[currentVariantIndex]);

            currentMapOverlay.Show();
            
            // Rendre l'overlay click-through
            MakeClickThrough(currentMapOverlay.Handle);

            AppendDebug("Map overlay shown (click-through) — stays until F2 is pressed.\r\n");
        }

        private void SetCurrentMapImage(string path)
        {
            try
            {
                // Dispose previous image if any
                if (currentMapPictureBox.Image != null)
                {
                    try { currentMapPictureBox.Image.Dispose(); } catch { }
                    currentMapPictureBox.Image = null;
                }

                // Load image without locking file by cloning
                Image loaded = Image.FromFile(path);
                Bitmap clone = new Bitmap(loaded);
                loaded.Dispose();

                currentMapPictureBox.Image = clone;
            }
            catch (Exception ex)
            {
                AppendDebug("Error loading variant image: " + ex.Message + "\r\n");
            }
        }

        private void AppendDebug(string message)
        {
            if (debugBox != null && !debugBox.IsDisposed)
            {
                // Si le message contient des indicateurs spécifiques, on peut changer la couleur
                if (message.Contains("❌") || message.Contains("Error"))
                {
                    // Pour les messages d'erreur, on stylise avec une couleur rouge
                    debugBox.SelectionStart = debugBox.TextLength;
                    debugBox.SelectionLength = 0;
                    debugBox.SelectionColor = Color.FromArgb(255, 100, 100);
                    debugBox.AppendText(message);
                    debugBox.SelectionColor = debugBox.ForeColor;
                }
                else if (message.Contains("✅") || message.Contains("found"))
                {
                    // Pour les messages de succès, on stylise avec une couleur verte
                    debugBox.SelectionStart = debugBox.TextLength;
                    debugBox.SelectionLength = 0;
                    debugBox.SelectionColor = Color.FromArgb(100, 255, 100);
                    debugBox.AppendText(message);
                    debugBox.SelectionColor = debugBox.ForeColor;
                }
                else
                {
                    // Messages standards
                    debugBox.AppendText(message);
                }
                
                // Scroll automatique vers la fin
                debugBox.SelectionStart = debugBox.Text.Length;
                debugBox.ScrollToCaret();
            }
        }

        // Gestion du slider d'opacité
        private void OpacitySlider_ValueChanged(object sender, EventArgs e)
        {
            float opacity = opacitySlider.Value / 100f;
            opacityLabel.Text = $"Opacity: {opacitySlider.Value}%";
            
            // Met à jour l'opacité de l'overlay s'il existe
            if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
            {
                currentMapOverlay.Opacity = opacity;
                AppendDebug($"Opacity adjusted to {opacity:P0}\r\n");
            }

            // Mémorise la valeur par défaut pour les futurs overlays
            defaultOverlayOpacity = opacity;
        }

        // Gestion du slider de taille
        private void SizeSlider_ValueChanged(object sender, EventArgs e)
        {
            int percentage = sizeSlider.Value;
            sizeLabel.Text = $"Size: {percentage}%";

            // Calcule les dimensions proportionnelles
            int width = (int)(defaultOverlaySize.Width * percentage / 100);
            int height = (int)(defaultOverlaySize.Height * percentage / 100);
            
            // Met à jour la taille de l'overlay s'il existe
            if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
            {
                // Sauvegarde la position actuelle pour la recalculer après
                Point oldLocation = currentMapOverlay.Location;
                
                // Change la taille
                currentMapOverlay.Size = new Size(width, height);
                
                // Recalcule la position en fonction de l'ancrage
                UpdateOverlayPosition(true);
                
                AppendDebug($"Size adjusted to {percentage}% ({width}x{height})\r\n");
            }
        }
        
        // Gestion du changement de position (boutons radio)
        private void RadioPosition_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Checked)
            {
                string newPosition;
                string positionText;
                
                if (sender == radioTopLeft)
                {
                    newPosition = "top-left";
                    positionText = "Top Left";
                }
                else if (sender == radioTopCenter)
                {
                    newPosition = "top-center";
                    positionText = "Top Center";
                }
                else if (sender == radioTopRight)
                {
                    newPosition = "top-right";
                    positionText = "Top Right";
                }
                else
                {
                    return; // Ne devrait pas arriver
                }
                
                currentPosition = newPosition;
                AppendDebug($"Position changed: {positionText}\r\n");
                
                // Met à jour la position de l'overlay s'il existe
                if (currentMapOverlay != null && !currentMapOverlay.IsDisposed)
                {
                    UpdateOverlayPosition(false);
                }
            }
        }
        
        // Méthode pour mettre à jour la position de l'overlay selon le choix
        private void UpdateOverlayPosition(bool fromSizeChange = false)
        {
            if (currentMapOverlay == null || currentMapOverlay.IsDisposed)
                return;
                
            int x = 20; // Position par défaut (haut gauche)
            int y = 20; // La hauteur est toujours fixée en haut
            
            switch (currentPosition)
            {
                case "top-left":
                    x = 20; // Marge par défaut
                    break;
                case "top-center":
                    // Centre horizontal
                    x = (Screen.PrimaryScreen.WorkingArea.Width - currentMapOverlay.Width) / 2;
                    break;
                case "top-right":
                    // Aligné à droite
                    x = Screen.PrimaryScreen.WorkingArea.Width - currentMapOverlay.Width - 20;
                    break;
            }
            
            currentMapOverlay.Location = new Point(x, y);
        }
        
        // Essaye plusieurs prétraitements et retourne le meilleur résultat OCR
        private string TryBestOcr(Bitmap bmp)
        {
            string bestResult = null;
            float bestMeanConfidence = 0;

            var variants = new List<Bitmap>();
            variants.Add((Bitmap)bmp.Clone()); // original
            variants.Add(AdjustBrightnessContrast(bmp, 1.3f, 0.1f)); // brighter + slight contrast
            variants.Add(AdjustBrightnessContrast(bmp, 1.6f, 0.2f)); // stronger
            variants.Add(InvertColors(bmp)); // inverted
            variants.Add(ResizeBitmap(bmp, 2.0f)); // scaled up

            using (var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default))
            {
                // Optional: restrict characters to typical map name chars
                try { engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'\u00C0\u00E0"); } catch { }

                foreach (var variant in variants)
                {
                    try
                    {
                        using (var img = PixConverter.ToPix(variant))
                        {
                            using (var page = engine.Process(img))
                            {
                                string text = page.GetText()?.Trim();
                                float confidence = page.GetMeanConfidence();
                                AppendDebug($"OCR attempt: \"{text}\" (Confidence: {confidence:P2})\r\n");

                                if (!string.IsNullOrWhiteSpace(text) && confidence > bestMeanConfidence)
                                {
                                    bestMeanConfidence = confidence;
                                    bestResult = text;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendDebug("OCR attempt error: " + ex.Message + "\r\n");
                    }
                }
            }

            // Dispose variants we created (keep original bmp provided by caller untouched)
            foreach (var v in variants)
            {
                try { v.Dispose(); } catch { }
            }

            return bestResult;
        }

        // Ajuste la luminosité et le contraste d'une image
        private Bitmap AdjustBrightnessContrast(Bitmap src, float brightness, float contrast)
        {
            Bitmap bmp = new Bitmap(src.Width, src.Height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Matrice de couleur pour ajuster la luminosité et le contraste
                float b = brightness - 1.0f;
                float c = contrast + 1.0f;

                var matrix = new ColorMatrix(new float[][]
                {
                    new float[] {c, 0, 0, 0, 0},
                    new float[] {0, c, 0, 0, 0},
                    new float[] {0, 0, c, 0, 0},
                    new float[] {0, 0, 0, 1, 0},
                    new float[] {b, b, b, 0, 1}
                });

                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(matrix);
                    g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }

            return bmp;
        }

        // Inverse les couleurs d'une image (négatif)
        private Bitmap InvertColors(Bitmap src)
        {
            Bitmap bmp = new Bitmap(src.Width, src.Height);

            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    Color c = src.GetPixel(x, y);
                    // Inversion simple
                    Color nc = Color.FromArgb(255 - c.R, 255 - c.G, 255 - c.B);
                    bmp.SetPixel(x, y, nc);
                }
            }

            return bmp;
        }

        // Redimensionne une image en maintenant les proportions
        private Bitmap ResizeBitmap(Bitmap src, float scale)
        {
            int newWidth = (int)(src.Width * scale);
            int newHeight = (int)(src.Height * scale);
            Bitmap bmp = new Bitmap(newWidth, newHeight);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, newWidth, newHeight);
            }

            return bmp;
        }
    }
}
