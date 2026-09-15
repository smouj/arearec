from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import subprocess
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from datetime import datetime

from .core import Region, build_ffmpeg_command, resolve_ffmpeg

APP_DIR = Path(__file__).resolve().parents[2]
CONFIG_PATH = APP_DIR / "arearec.json"

# Ask Windows to not DPI-scale our window so coordinates stay 1:1.
if os.name == "nt":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def _load_config() -> dict:
    try:
        return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def _save_config(cfg: dict) -> None:
    CONFIG_PATH.write_text(json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


class RegionSelector(tk.Toplevel):
    """Full-screen translucent overlay for region selection."""

    def __init__(self, parent: tk.Tk, on_selected, on_cancel, initial_region: Region | None = None):
        super().__init__(parent)
        self.parent = parent
        self.on_selected = on_selected
        self.on_cancel = on_cancel
        self.start_x = 0
        self.start_y = 0
        self.rect = None

        self.withdraw()
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.attributes("-alpha", 0.35)
        self.configure(bg="black")

        sw = self.winfo_screenwidth()
        sh = self.winfo_screenheight()
        self.geometry(f"{sw}x{sh}+0+0")

        self.canvas = tk.Canvas(self, bg="black", highlightthickness=0, cursor="cross")
        self.canvas.pack(fill="both", expand=True)

        # Show previous region as hint if available
        if initial_region and initial_region.valid:
            r = initial_region
            self.canvas.create_rectangle(
                r.x, r.y, r.x + r.width, r.y + r.height,
                outline="#555555", width=2, dash=(6, 4),
            )
            self.canvas.create_text(
                r.x + r.width // 2, r.y + r.height // 2,
                text="Región anterior",
                fill="#888888", font=("Segoe UI", 10),
            )

        self.canvas.create_text(
            sw // 2, 36,
            text="ARRASTRA PARA SELECCIONAR · ESC PARA CANCELAR",
            fill="white", font=("Segoe UI", 13, "bold"),
        )
        self.canvas.bind("<ButtonPress-1>", self._start)
        self.canvas.bind("<B1-Motion>", self._drag)
        self.canvas.bind("<ButtonRelease-1>", self._end)
        self.bind("<Escape>", lambda _e: self._cancel())
        self.deiconify()
        self.focus_force()

    def _start(self, event):
        self.start_x, self.start_y = event.x, event.y
        if self.rect:
            self.canvas.delete(self.rect)
        self.rect = self.canvas.create_rectangle(
            self.start_x, self.start_y, self.start_x, self.start_y,
            outline="#00ff00", width=3,
        )

    def _drag(self, event):
        if self.rect:
            self.canvas.coords(self.rect, self.start_x, self.start_y, event.x, event.y)

    def _end(self, event):
        region = Region.from_points(self.start_x, self.start_y, event.x, event.y)
        self.destroy()
        if region.valid:
            self.parent.after_idle(lambda: self.on_selected(region))
        else:
            self.parent.after_idle(self.on_cancel)

    def _cancel(self):
        self.destroy()
        self.parent.after_idle(self.on_cancel)


class RegionHighlight(tk.Toplevel):
    """Border-only overlay showing the selected capture region before recording."""

    def __init__(self, parent: tk.Tk, region: Region):
        super().__init__(parent)
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.attributes("-transparentcolor", "white")
        self.configure(bg="white")

        # 3-pixel green border: place a frame with a hole
        border = 3
        self.geometry(
            f"{region.width + border * 2}x{region.height + border * 2}"
            f"+{region.x - border}+{region.y - border}"
        )

        # Outer frame (green), inner hole (transparent = white, set as -transparentcolor)
        outer = tk.Frame(self, bg="#00cc00", width=region.width + border * 2, height=region.height + border * 2)
        outer.place(x=0, y=0, relwidth=1, relheight=1)
        inner = tk.Frame(self, bg="white", width=region.width, height=region.height)
        inner.place(x=border, y=border, width=region.width, height=region.height)

        # Dimensions label
        label = tk.Label(
            self, text=f"  {region.width}×{region.height}  ",
            bg="#00cc00", fg="white", font=("Segoe UI", 9, "bold"),
        )
        label.place(x=0, y=-20)

    def clear(self):
        self.destroy()


class AreaRecApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("AreaRec")
        self.geometry("440x380")
        self.resizable(False, False)
        self.region: Region | None = None
        self.process: subprocess.Popen | None = None
        self.highlight: RegionHighlight | None = None

        # Persistent config
        cfg = _load_config()
        self._last_dir = cfg.get("last_dir", str(APP_DIR / "recordings"))
        self.fps = tk.IntVar(value=cfg.get("fps", 30))
        self.draw_mouse = tk.BooleanVar(value=cfg.get("draw_mouse", True))
        self.status = tk.StringVar(value="Listo")
        self.region_text = tk.StringVar(value="Sin seleccionar")

        # Restore last region if saved
        if "region" in cfg:
            try:
                self.region = Region.from_dict(cfg["region"])
                self.region_text.set(f"Región: {self.region}")
            except (KeyError, ValueError):
                self.region = None

        self._style()
        self._build()
        self.protocol("WM_DELETE_WINDOW", self._close)

    def _style(self):
        style = ttk.Style(self)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        style.configure("Title.TLabel", font=("Segoe UI", 20, "bold"))
        style.configure("Sub.TLabel", font=("Segoe UI", 9))
        style.configure("Rec.TButton", font=("Segoe UI", 11, "bold"), padding=9)
        style.configure("Stop.TButton", font=("Segoe UI", 11, "bold"), padding=9)
        style.configure("Dim.TLabel", font=("Segoe UI", 8), foreground="#888888")

    def _build(self):
        root = ttk.Frame(self, padding=22)
        root.pack(fill="both", expand=True)
        ttk.Label(root, text="AreaRec", style="Title.TLabel").pack(anchor="w")
        ttk.Label(root, text="Graba exactamente la región que selecciones.", style="Sub.TLabel").pack(anchor="w", pady=(0, 14))

        # Region selection row
        sel_frame = ttk.Frame(root)
        sel_frame.pack(fill="x", pady=(0, 4))
        ttk.Button(sel_frame, text="Seleccionar área", command=self._select).pack(side="left", fill="x", expand=True)

        self.region_label = ttk.Label(root, textvariable=self.region_text, style="Sub.TLabel")
        self.region_label.pack(anchor="w", pady=(2, 12))

        # Options
        opts = ttk.LabelFrame(root, text="Configuración", padding=10)
        opts.pack(fill="x", pady=(0, 8))

        row0 = ttk.Frame(opts)
        row0.pack(fill="x", pady=(0, 6))
        ttk.Label(row0, text="FPS").pack(side="left")
        ttk.Combobox(row0, textvariable=self.fps, values=(30, 60), width=5, state="readonly").pack(side="left", padx=(8, 20))
        ttk.Checkbutton(row0, text="Mostrar cursor", variable=self.draw_mouse).pack(side="left")

        row1 = ttk.Frame(opts)
        row1.pack(fill="x")
        ttk.Label(row1, text="Carpeta").pack(side="left")
        self.dir_label = ttk.Label(row1, text=self._short_dir(), style="Dim.TLabel", width=30, anchor="w")
        self.dir_label.pack(side="left", padx=(8, 4))
        ttk.Button(row1, text="…", width=3, command=self._choose_dir).pack(side="left")

        # Record button
        self.record_btn = ttk.Button(root, text="●  GRABAR", style="Rec.TButton", command=self._toggle_recording)
        self.record_btn.pack(fill="x", pady=(12, 6))
        ttk.Label(root, textvariable=self.status).pack(anchor="center")

    def _short_dir(self) -> str:
        d = self._last_dir
        if len(d) > 38:
            return "…" + d[-35:]
        return d

    def _choose_dir(self):
        chosen = filedialog.askdirectory(
            title="Carpeta de guardado",
            initialdir=self._last_dir,
        )
        if chosen:
            self._last_dir = chosen
            self.dir_label.configure(text=self._short_dir())
            self._save_config()

    def _save_config(self):
        cfg: dict = {
            "fps": self.fps.get(),
            "draw_mouse": self.draw_mouse.get(),
            "last_dir": self._last_dir,
        }
        if self.region and self.region.valid:
            cfg["region"] = self.region.to_dict()
        _save_config(cfg)

    def _select(self):
        if self.process:
            return
        # Remove any existing highlight
        if self.highlight:
            self.highlight.clear()
            self.highlight = None
        self.withdraw()
        self.after(120, lambda: RegionSelector(self, self._selected, self._selection_cancelled, self.region))

    def _selection_cancelled(self):
        self.deiconify()
        self.lift()
        # Restore highlight if we still have a region
        if self.region and self.region.valid:
            self._show_highlight(self.region)

    def _selected(self, region: Region):
        self.region = region
        self.deiconify()
        self.lift()
        self.region_text.set(f"Región: {region}")
        self._save_config()
        self._show_highlight(region)

    def _show_highlight(self, region: Region):
        """Show a green border overlay on the selected region."""
        if self.highlight:
            self.highlight.clear()
        try:
            self.highlight = RegionHighlight(self, region)
        except Exception:
            self.highlight = None

    def _default_name(self) -> str:
        return f"recording_{datetime.now():%Y-%m-%d_%H-%M-%S}.mp4"

    def _toggle_recording(self):
        if self.process:
            self._stop()
        else:
            self._start()

    def _start(self):
        if not self.region:
            messagebox.showinfo("AreaRec", "Selecciona primero una región de la pantalla.")
            return
        ffmpeg = resolve_ffmpeg(APP_DIR)
        if not ffmpeg:
            messagebox.showerror("FFmpeg no encontrado", "Instala FFmpeg o copia ffmpeg.exe en la carpeta bin/.")
            return

        # Use saved directory as default
        default_dir = Path(self._last_dir)
        default_dir.mkdir(parents=True, exist_ok=True)
        output = filedialog.asksaveasfilename(
            title="Guardar grabación",
            initialdir=str(default_dir),
            initialfile=self._default_name(),
            defaultextension=".mp4",
            filetypes=[("Vídeo MP4", "*.mp4")],
        )
        if not output:
            return

        # Remember the chosen directory
        self._last_dir = str(Path(output).parent)
        self.dir_label.configure(text=self._short_dir())
        self._save_config()

        cmd = build_ffmpeg_command(ffmpeg, self.region, self.fps.get(), Path(output), self.draw_mouse.get())
        creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            self.process = subprocess.Popen(
                cmd,
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=creationflags,
            )
        except OSError as exc:
            messagebox.showerror("No se pudo iniciar", str(exc))
            self.process = None
            return

        self.status.set("● REC · grabando región")
        self.record_btn.configure(text="■  DETENER", style="Stop.TButton")

    def _stop(self):
        if not self.process:
            return
        proc = self.process
        self.process = None
        try:
            if proc.stdin:
                proc.stdin.write(b"q\n")
                proc.stdin.flush()
            proc.wait(timeout=5)
        except Exception:
            proc.terminate()
        self.status.set("Grabación guardada")
        self.record_btn.configure(text="●  GRABAR", style="Rec.TButton")

    def _close(self):
        if self.process:
            self._stop()
        self._save_config()
        if self.highlight:
            self.highlight.clear()
        self.destroy()


def main():
    AreaRecApp().mainloop()


if __name__ == "__main__":
    main()