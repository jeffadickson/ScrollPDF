#!/usr/bin/env python3
"""Compare a reconstructed PDF to a reference using registered grayscale images.

Diagnostic only: different margins, scaling and antialiasing affect metrics.
Requires: PyMuPDF, opencv-python, numpy.
"""
import argparse, csv, json
from pathlib import Path
import cv2, fitz, numpy as np

def render(doc, index, width=650):
    page=doc[index]
    pix=page.get_pixmap(matrix=fitz.Matrix(width/page.rect.width,width/page.rect.width),
                        colorspace=fitz.csGRAY,alpha=False)
    return np.frombuffer(pix.samples,np.uint8).reshape(pix.height,pix.width).copy()

def detect_defect_bands(reference, aligned, valid, threshold=32, minimum_rows=4):
    """Find concentrated horizontal disagreement; diagnostic, not proof of a seam."""
    difference=cv2.absdiff(reference,aligned)
    informative=((reference<238)|(aligned<238))&valid
    coverage=informative.sum(axis=1)
    row_error=np.divide(((difference>threshold)&informative).sum(axis=1),
                        coverage,out=np.zeros(len(coverage),dtype=float),
                        where=coverage>=30)
    # A narrow abnormal row band relative to nearby content is more meaningful
    # than a uniformly elevated difference caused by antialiasing.
    baseline=cv2.GaussianBlur(row_error.astype(np.float32)[:,None],(1,61),0)[:,0]
    unusual=(row_error>np.maximum(.23,baseline+.17))&(coverage>=30)
    bands=[]; first=None
    for y in range(len(unusual)+1):
        flagged=bool(unusual[y]) if y<len(unusual) else False
        if flagged and first is None:first=y
        if not flagged and first is not None:
            if y-first>=minimum_rows:
                bands.append({'top':int(first),'bottom':int(y),
                              'peak_fraction':round(float(row_error[first:y].max()),3)})
            first=None
    return bands,row_error


def match(ref, actual, limit=45):
    """Register uniform scale and translation without warping content locally."""
    h,w=ref.shape
    a=cv2.GaussianBlur(ref,(3,3),0)
    base=cv2.resize(actual,(w,h),interpolation=cv2.INTER_AREA)
    best=None
    # A bounded zoom sweep is robust to Fit Width versus Fit Page offsets.
    for scale in np.linspace(.96,1.04,17):
        matrix=np.float32([[scale,0,(1-scale)*w/2],[0,scale,(1-scale)*h/2]])
        scaled=cv2.warpAffine(base,matrix,(w,h),borderValue=255)
        edge_a=cv2.Laplacian(a,cv2.CV_32F)
        edge_b=cv2.Laplacian(cv2.GaussianBlur(scaled,(3,3),0),cv2.CV_32F)
        (dx,dy),response=cv2.phaseCorrelate(edge_a,edge_b)
        # Phase correlation returns displacement of actual relative to reference.
        # Apply its negative to move actual back onto the reference.
        for sx,sy in [(-round(dx),-round(dy)),(0,0)]:
            if abs(sx)>limit or abs(sy)>limit:continue
            shifted=cv2.warpAffine(scaled,np.float32([[1,0,sx],[0,1,sy]]),
                                   (w,h),borderValue=255)
            valid=np.ones((h,w),np.uint8)*255
            valid=cv2.warpAffine(valid,matrix,(w,h),borderValue=0)
            valid=cv2.warpAffine(valid,np.float32([[1,0,sx],[0,1,sy]]),
                                 (w,h),borderValue=0)>0
            ink=(ref<225)&valid
            if ink.sum()<250:continue
            diff=cv2.absdiff(ref,shifted)
            error=float(np.minimum(diff[ink],100).mean())
            if best is None or error<best[0]:
                best=(error,shifted,valid,(int(sx),int(sy)),float(scale),float(response))
    if best is None:raise ValueError("No registration with sufficient image content")
    error,aligned,valid,offset,scale,response=best
    return aligned,valid,offset,error,scale,response

def compare(reference, reconstructed, output, width=650):
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    reference=fitz.open(reference);reconstructed=fitz.open(reconstructed)
    pages=[]
    for i in range(min(len(reference),len(reconstructed))):
        a=render(reference,i,width);b=render(reconstructed,i,width)
        aligned,valid,(x,y),error,scale,response=match(a,b)
        diff=cv2.absdiff(a,aligned)
        heat=cv2.applyColorMap(np.minimum(diff.astype(np.uint16)*3,255).astype(np.uint8),cv2.COLORMAP_TURBO)
        heat[~valid]=(40,40,40)
        cv2.imwrite(str(output/f'page-{i+1:02d}-diff.png'),heat)
        ink=(a<225)&valid
        bands,_=detect_defect_bands(a,aligned,valid)
        pages.append({'page':i+1,'dx_px':x,'dy_px':y,
                      'scale':round(scale,5),'registration_response':round(response,4),
                      'suspect_bands':bands,'suspect_band_count':len(bands),
                      'mae':round(float(diff[valid].mean()),3),
                      'ink_mae':round(float(diff[ink].mean()),3) if ink.any() else None,
                      'different_gt30_pct':round(100*float(np.mean(diff[valid]>30)),3),
                      'overlap_pct':round(100*float(valid.mean()),2),
                      'registration_error':round(error,3)})
    result={'reference_pages':len(reference),'reconstructed_pages':len(reconstructed),
            'page_count_matches':len(reference)==len(reconstructed),
            'pages':pages,
            'warning':'Scale and translation were optimized over bounded candidates. Suspect bands are diagnostic only; content differences and text antialiasing may create false positives.'}
    (output/'comparison.json').write_text(json.dumps(result,indent=2))
    if pages:
        with (output/'pages.csv').open('w',newline='') as file:
            writer=csv.DictWriter(file,fieldnames=pages[0].keys());writer.writeheader();writer.writerows([{**p,'suspect_bands':json.dumps(p['suspect_bands'])} for p in pages])
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reference');parser.add_argument('reconstructed')
    parser.add_argument('--output',default='comparison');parser.add_argument('--width',type=int,default=650)
    args=parser.parse_args()
    result=compare(args.reference,args.reconstructed,args.output,args.width)
    print(json.dumps({'page_count_matches':result['page_count_matches'],
                      'reference_pages':result['reference_pages'],
                      'reconstructed_pages':result['reconstructed_pages']},indent=2))
