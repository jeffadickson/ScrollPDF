"""Smoke tests for reference-PDF comparison. Run: python -m unittest discover -s tests_py"""
import sys, tempfile, unittest
from pathlib import Path
import fitz

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"tools"))
from compare_reference_pdf import compare

class ReferenceComparisonTests(unittest.TestCase):
    def test_identical_pdf_near_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            document=fitz.open()
            page=document.new_page()
            page.insert_text((90,110),"ScrollPDF fidelity regression",fontsize=22)
            page.draw_rect(fitz.Rect(90,140,330,220),color=(0,0,0),fill=(.5,.5,.5))
            path=Path(tmp)/"reference.pdf";document.save(path);document.close()
            result=compare(str(path),str(path),str(Path(tmp)/"result"),650)
            self.assertTrue(result["page_count_matches"])
            self.assertLess(result["pages"][0]["mae"],.02)
            self.assertTrue((Path(tmp)/"result"/"page-01-diff.png").exists())

    def test_missing_page_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            full=fitz.open();full.new_page();full.new_page()
            short=fitz.open();short.new_page()
            a=Path(tmp)/"full.pdf";b=Path(tmp)/"short.pdf"
            full.save(a);short.save(b);full.close();short.close()
            result=compare(str(a),str(b),str(Path(tmp)/"result"))
            self.assertFalse(result["page_count_matches"])
            self.assertEqual(result["reference_pages"],2)
            self.assertEqual(result["reconstructed_pages"],1)

if __name__=="__main__":
    unittest.main()
