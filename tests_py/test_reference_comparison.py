"""Smoke tests for reference-PDF comparison. Run: python -m unittest discover -s tests_py"""
import sys, tempfile, unittest
from pathlib import Path
import fitz

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"tools"))
from compare_reference_pdf import compare, match, detect_defect_bands
import cv2
import numpy as np

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

    def test_scale_registration(self):
        reference=np.full((440,340),255,np.uint8)
        cv2.putText(reference,"TEST",(48,155),cv2.FONT_HERSHEY_SIMPLEX,1.6,0,3)
        cv2.rectangle(reference,(65,210),(290,300),0,3)
        transform=np.float32([[1.02,0,-3],[0,1.02,4]])
        shifted=cv2.warpAffine(reference,transform,(340,440),borderValue=255)
        aligned,valid,offset,error,scale,response=match(reference,shifted)
        self.assertGreater(valid.mean(),.8)
        self.assertLess(error,35)
        self.assertTrue(.96<=scale<=1.04)

    def test_localized_defect_band(self):
        reference=np.full((280,320),255,np.uint8)
        for y in range(20,260,8):
            cv2.line(reference,(20,y),(300,y),0,1)
        damaged=reference.copy()
        damaged[120:132,20:301]=0
        bands,_=detect_defect_bands(reference,damaged,np.ones(reference.shape,bool),
                                    minimum_rows=3)
        self.assertTrue(any(b['top']<=125<b['bottom'] for b in bands),bands)

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
