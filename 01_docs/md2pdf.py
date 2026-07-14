"""Convert 功能说明文档.md to PDF using fpdf2."""
import os
import re
from fpdf import FPDF

DOCS_DIR = os.path.dirname(os.path.abspath(__file__))
MD_FILE = os.path.join(DOCS_DIR, "功能说明文档.md")
PDF_FILE = os.path.join(DOCS_DIR, "功能说明文档.pdf")

# Font paths
FONT_DIR = r"C:\Windows\Fonts"
FONT_SONG = os.path.join(FONT_DIR, "simsun.ttc")
FONT_HEI = os.path.join(FONT_DIR, "simhei.ttf")

class MarkdownPDF(FPDF):
    def __init__(self):
        super().__init__("P", "mm", "A4")
        # Register Chinese fonts
        self.add_font("Song", "", FONT_SONG)
        self.add_font("Hei", "", FONT_HEI)
        self.set_auto_page_break(True, 20)

        # Margins
        self.set_left_margin(22)
        self.set_right_margin(20)
        self.w_avail = self.w - self.l_margin - self.r_margin  # available width

        # State
        self.in_code_block = False
        self.in_table = False
        self.table_rows = []
        self.table_col_widths = []
        self.page_count = 0

    def header(self):
        if self.page_no() == 1:
            return  # title page handled differently
        self.set_font("Song", "", 8)
        self.set_text_color(150, 150, 150)
        self.cell(0, 5, "道岔监测系统 (SwitchMonitor) 功能说明文档", align="L")
        self.cell(0, 5, f"— {self.page_no()} —", align="R", new_x="LMARGIN", new_y="NEXT")
        self.line(self.l_margin, self.get_y(), self.w - self.r_margin, self.get_y())
        self.ln(4)

    def footer(self):
        pass  # handled in header for simplicity

    # ── helpers ──
    def write_body(self, text, size=10.5, bold=False, color=(34, 34, 34)):
        self.set_font("Song", "", size)
        if bold:
            self.set_font("Song", "", size)
        self.set_text_color(*color)
        self.set_font_size(size)
        self.multi_cell(self.w_avail, 6.5, text, align="L")
        self.ln(1)

    def write_h1(self, text):
        self.ln(4)
        self.set_draw_color(26, 82, 118)
        self.set_line_width(0.6)
        y = self.get_y()
        self.set_font("Hei", "", 17)
        self.set_text_color(26, 82, 118)
        self.multi_cell(self.w_avail, 10, text, align="C")
        self.line(self.l_margin, self.get_y() + 1, self.w - self.r_margin, self.get_y() + 1)
        self.ln(6)

    def write_h2(self, text):
        self.ln(4)
        self.set_draw_color(36, 113, 163)
        self.set_line_width(0.4)
        self.set_font("Hei", "", 13.5)
        self.set_text_color(36, 113, 163)
        self.multi_cell(self.w_avail, 8, text, align="L")
        self.line(self.l_margin, self.get_y() + 0.5, self.w - self.r_margin, self.get_y() + 0.5)
        self.ln(4)

    def write_h3(self, text):
        self.ln(3)
        self.set_font("Hei", "", 12)
        self.set_text_color(44, 62, 80)
        self.multi_cell(self.w_avail, 7.5, text, align="L")
        self.ln(2)

    def write_h4(self, text):
        self.ln(2)
        self.set_font("Hei", "", 11)
        self.set_text_color(44, 62, 80)
        self.multi_cell(self.w_avail, 7, text, align="L")
        self.ln(1)

    def write_para(self, text):
        self.set_font("Song", "", 10.5)
        self.set_text_color(34, 34, 34)
        self.multi_cell(self.w_avail, 6.5, text, align="L")
        self.ln(1)

    def write_ul(self, text):
        self.set_font("Song", "", 10.5)
        self.set_text_color(34, 34, 34)
        indent = 6
        bullet = "•"
        self.set_x(self.l_margin + indent)
        self.cell(5, 6.5, bullet)
        self.multi_cell(self.w_avail - indent - 5, 6.5, text, align="L")
        self.ln(0.5)

    def write_ol(self, num, text):
        self.set_font("Song", "", 10.5)
        self.set_text_color(34, 34, 34)
        indent = 6
        self.set_x(self.l_margin + indent)
        self.cell(7, 6.5, f"{num}.")
        self.multi_cell(self.w_avail - indent - 7, 6.5, text, align="L")
        self.ln(0.5)

    def write_code_block(self, lines):
        self.set_fill_color(245, 245, 245)
        self.set_draw_color(36, 113, 163)
        self.set_line_width(0.3)

        # Calculate block height
        line_h = 4.8
        block_h = len(lines) * line_h + 6
        if block_h > 180:
            block_h = 180  # cap for huge blocks

        self.set_font("Song", "", 8.5)
        self.set_text_color(60, 60, 60)

        y_start = self.get_y()
        x_start = self.l_margin

        # Draw background and left border
        self.set_fill_color(248, 248, 248)
        self.rect(x_start, y_start, self.w_avail, block_h, "F")
        self.set_fill_color(36, 113, 163)
        self.rect(x_start, y_start, 2.5, block_h, "F")

        self.set_xy(x_start + 7, y_start + 3)
        for i, line in enumerate(lines):
            if i > 0 and self.get_y() - y_start > block_h - 5:
                self.set_xy(x_start + 7, self.get_y())
                self.set_font("Song", "", 8.5)
                self.cell(self.w_avail - 12, line_h, "...")
                break
            self.set_x(x_start + 7)
            # Truncate long lines
            display = line[:130] if len(line) > 130 else line
            self.cell(self.w_avail - 12, line_h, display)
            self.ln(line_h)

        self.set_y(y_start + block_h + 4)

    def write_table(self, rows):
        """rows[0] = header, rows[1:] = data"""
        if not rows:
            return

        self.ln(2)
        ncols = len(rows[0])

        # Calculate column widths
        # Estimate max content width per column
        max_chars = [0] * ncols
        for row in rows:
            for i, cell in enumerate(row):
                char_count = len(str(cell))
                if char_count > max_chars[i]:
                    max_chars[i] = char_count

        # Proportional allocation
        total_chars = sum(max_chars) or 1
        col_widths = []
        for mc in max_chars:
            w = (mc / total_chars) * self.w_avail
            w = max(w, 18)  # minimum width
            w = min(w, self.w_avail * 0.55)  # max 55% for one column
            col_widths.append(w)

        # Draw header
        self.set_fill_color(36, 113, 163)
        self.set_text_color(255, 255, 255)
        self.set_font("Hei", "", 9)
        self.set_draw_color(200, 200, 200)
        self.set_line_width(0.2)

        line_h = 6.5
        x0 = self.l_margin
        y0 = self.get_y()

        for i, cell in enumerate(rows[0]):
            self.set_xy(x0, y0)
            self.set_font("Hei", "", 9)
            self.set_text_color(255, 255, 255)
            self.set_fill_color(36, 113, 163)
            self.cell(col_widths[i], line_h + 2, str(cell), border=1, fill=True)
            x0 += col_widths[i]

        self.set_y(y0 + line_h + 2)

        # Draw data rows
        for row_idx, row in enumerate(rows[1:]):
            x0 = self.l_margin
            y_row = self.get_y()

            # Fill alternating rows
            if row_idx % 2 == 0:
                self.set_fill_color(242, 247, 251)
            else:
                self.set_fill_color(255, 255, 255)

            max_h = line_h
            for i, cell in enumerate(row):
                self.set_xy(x0, y_row)
                self.set_font("Song", "", 8.5)
                self.set_text_color(51, 51, 51)
                self.cell(col_widths[i], line_h + 2, str(cell), border=1, fill=True)
                x0 += col_widths[i]

            self.set_y(y_row + line_h + 2)

        self.ln(3)

    def write_hr(self):
        self.ln(2)
        self.set_draw_color(200, 200, 200)
        self.set_line_width(0.3)
        y = self.get_y()
        self.line(self.l_margin + 20, y, self.w - self.r_margin - 20, y)
        self.ln(4)

    def write_blockquote(self, text):
        self.set_fill_color(234, 242, 248)
        self.set_draw_color(36, 113, 163)
        self.set_line_width(0.4)

        self.set_font("Song", "", 10)
        self.set_text_color(26, 82, 118)

        # Measure text height
        lines = self.multi_cell(self.w_avail - 12, 6, text, dry_run=True, output="LINES")
        block_h = max(len(lines) * 6 + 6, 12)

        y_start = self.get_y()
        x_start = self.l_margin

        # Background
        self.set_fill_color(242, 247, 252)
        self.rect(x_start, y_start, self.w_avail, block_h, "F")
        # Left border
        self.set_fill_color(36, 113, 163)
        self.rect(x_start, y_start, 3, block_h, "F")

        self.set_xy(x_start + 8, y_start + 3)
        self.set_font("Song", "", 10)
        self.set_text_color(26, 82, 118)
        self.multi_cell(self.w_avail - 12, 6, text, align="L")
        self.ln(2)

# ── Parse and render ──
pdf = MarkdownPDF()
pdf.set_title("道岔监测系统 (SwitchMonitor) 功能说明文档")
pdf.add_page()

with open(MD_FILE, "r", encoding="utf-8") as f:
    lines = f.readlines()

# Remove BOM if present
if lines and lines[0].startswith("﻿"):
    lines[0] = lines[0][1:]

i = 0
n = len(lines)
ol_counter = 0

while i < n:
    line = lines[i]

    # Code blocks
    if line.strip().startswith("```"):
        code_lines = []
        i += 1
        while i < n and not lines[i].strip().startswith("```"):
            code_lines.append(lines[i].rstrip())
            i += 1
        if code_lines:
            pdf.write_code_block(code_lines)
        i += 1
        ol_counter = 0
        continue

    # Tables
    if "|" in line and line.strip().startswith("|") and line.strip().endswith("|"):
        table_lines = []
        while i < n and "|" in lines[i] and lines[i].strip().startswith("|"):
            table_lines.append(lines[i])
            i += 1

        # Parse table: skip separator rows (|---|---|)
        rows = []
        ncols = 0
        for tl in table_lines:
            cells = [c.strip() for c in tl.strip().split("|")[1:-1]]
            # Remove empty leading/trailing cells from split artifacts
            while cells and cells[-1] == "":
                cells.pop()
            while cells and cells[0] == "":
                cells.pop(0)
            if not cells:
                continue
            if all(re.match(r"^[-:]+$", c) for c in cells):
                continue  # separator row
            if ncols == 0:
                ncols = len(cells)
            # Pad or truncate to match column count
            while len(cells) < ncols:
                cells.append("")
            cells = cells[:ncols]
            rows.append(cells)

        if rows:
            pdf.write_table(rows)
        ol_counter = 0
        continue

    # Blockquote
    if line.strip().startswith("> "):
        quote_parts = []
        while i < n and lines[i].strip().startswith(">"):
            text = lines[i].strip()
            if text.startswith("> "):
                text = text[2:]
            elif text.startswith(">"):
                text = text[1:]
            quote_parts.append(text)
            i += 1
        pdf.write_blockquote(" ".join(quote_parts))
        ol_counter = 0
        continue

    # Horizontal rule
    if line.strip() in ("---", "***", "___", "* * *"):
        pdf.write_hr()
        i += 1
        ol_counter = 0
        continue

    # Headings
    if line.startswith("# ") and not line.startswith("## "):
        text = re.sub(r"^#\s+", "", line).strip()
        pdf.write_h1(text)
        i += 1
        ol_counter = 0
        continue

    if line.startswith("## ") and not line.startswith("### "):
        text = re.sub(r"^##\s+", "", line).strip()
        pdf.write_h2(text)
        i += 1
        ol_counter = 0
        continue

    if line.startswith("### ") and not line.startswith("#### "):
        text = re.sub(r"^###\s+", "", line).strip()
        pdf.write_h3(text)
        i += 1
        ol_counter = 0
        continue

    if line.startswith("#### "):
        text = re.sub(r"^####\s+", "", line).strip()
        pdf.write_h4(text)
        i += 1
        ol_counter = 0
        continue

    # Ordered list
    ol_match = re.match(r"^(\s*)(\d+)\.\s+(.*)", line)
    if ol_match:
        indent = len(ol_match.group(1))
        text = ol_match.group(3)
        # Process inline markers
        text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
        text = re.sub(r"`([^`]+)`", r"\1", text)
        text = re.sub(r"\[(.+?)\]\(.+?\)", r"\1", text)
        text = re.sub(r"^[-–•]\s*", "", text)
        pdf.write_ol(ol_match.group(2), text)
        ol_counter = int(ol_match.group(2))
        i += 1
        continue

    # Unordered list
    ul_match = re.match(r"^(\s*)[-–*•]\s+(.*)", line)
    if ul_match:
        text = ul_match.group(2)
        text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
        text = re.sub(r"`([^`]+)`", r"\1", text)
        text = re.sub(r"\[(.+?)\]\(.+?\)", r"\1", text)
        pdf.write_ul(text)
        i += 1
        ol_counter = 0
        continue

    # Empty line
    if not line.strip():
        pdf.ln(2)
        i += 1
        continue

    # Regular paragraph - accumulate until blank line or special
    para_lines = []
    while i < n and lines[i].strip() and not lines[i].strip().startswith("#") \
            and not lines[i].strip().startswith("```") \
            and not lines[i].strip().startswith("|") \
            and not lines[i].strip().startswith("> ") \
            and not re.match(r"^(\s*)[-–*•]\s+", lines[i]) \
            and not re.match(r"^(\s*)\d+\.\s+", lines[i]) \
            and lines[i].strip() != "---":
        para_lines.append(lines[i].strip())
        i += 1

    if para_lines:
        text = " ".join(para_lines)
        # Strip common markdown markers
        text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
        text = re.sub(r"`([^`]+)`", r"\1", text)
        text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
        text = re.sub(r"!\[.*?\]\(.*?\)", "", text)
        text = re.sub(r"<!--.*?-->", "", text)
        text = text.strip()
        if text:
            pdf.write_para(text)
    else:
        i += 1

    ol_counter = 0

# Save
pdf.output(PDF_FILE)
print(f"PDF generated: {PDF_FILE}")
print(f"Pages: {pdf.page_no()}")
print(f"Size: {os.path.getsize(PDF_FILE) / 1024:.1f} KB")
