from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from datetime import datetime

from .core import Region, build_ffmpeg_command, resolve_ffmpeg

APP_DIR = Path(__file__).resolve().parents[2]


class RegionSelector(tk.Toplevel):
    def __init__(self, parent: tk.Tk, on_selected, on_cancel):
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
        self.canvas.create_text(
            sw // 2,
            36,
            text="ARRASTRA PARA SELECCIONAR · ESC PARA CANCELAR",
            fill="white",
            font=("Segoe UI", 13, "bold"),
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
            self.start_x,
            self.start_y,
            self.start_x,
            self.start_y,
            outline="white",
            width=3,
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


class AreaRecApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("AreaRec")
        self.geometry("420x315")
        self.resizable(False, False)
        self.region: Region | None = None
        self.process: subprocess.Popen | None = None

        self.fps = tk.IntVar(value=30)
        self.draw_mouse = tk.BooleanVar(value=True)
        self.status = tk.StringVar(value="Listo")
        self.region_text = tk.StringVar(value="Sin seleccionar")

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

    def _build(self):
        root = ttk.Frame(self, padding=22)
        root.pack(fill="both", expand=True)
        ttk.Label(root, text="AreaRec", style="Title.TLabel").pack(anchor="w")
        ttk.Label(root, text="Graba exactamente la región que selecciones.", style="Sub.TLabel").pack(anchor="w", pady=(0, 18))

        ttk.Button(root, text="Seleccionar área", command=self._select).pack(fill="x")
        ttk.Label(root, textvariable=self.region_text).pack(anchor="w", pady=(8, 14))

        opts = ttk.Frame(root)
        opts.pack(fill="x")
        ttk.Label(opts, text="FPS").grid(row=0, column=0, sticky="w")
        ttk.Combobox(opts, textvariable=self.fps, values=(30, 60), width=7, state="readonly").grid(row=0, column=1, padx=(8, 24))
        ttk.Checkbutton(opts, text="Mostrar cursor", variable=self.draw_mouse).grid(row=0, column=2, sticky="w")

        self.record_btn = ttk.Button(root, text="●  GRABAR", style="Rec.TButton", command=self._toggle_recording)
        self.record_btn.pack(fill="x", pady=(22, 8))
        ttk.Label(root, textvariable=self.status).pack(anchor="center")

    def _select(self):
        if self.process:
            return
        self.withdraw()
        self.after(120, lambda: RegionSelector(self, self._selected, self._selection_cancelled))

    def _selection_cancelled(self):
        self.deiconify()
        self.lift()

    def _selected(self, region: Region):
        self.region = region
        self.deiconify()
        self.lift()
        self.region_text.set(f"Región: {region.width} × {region.height}  ·  X {region.x}, Y {region.y}")

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

        default_dir = APP_DIR / "recordings"
        default_dir.mkdir(exist_ok=True)
        output = filedialog.asksaveasfilename(
            title="Guardar grabación",
            initialdir=default_dir,
            initialfile=self._default_name(),
            defaultextension=".mp4",
            filetypes=[("Vídeo MP4", "*.mp4")],
        )
        if not output:
            return

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
        self.record_btn.configure(text="■  DETENER")

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
        self.record_btn.configure(text="●  GRABAR")

    def _close(self):
        if self.process:
            self._stop()
        self.destroy()


def main():
    AreaRecApp().mainloop()


if __name__ == "__main__":
    main()
