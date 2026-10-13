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

def match(ref, actual, limit=45):
    h,w=ref.shape
    actual=cv2.resize(actual,(w,h),interpolation=cv2.INTER_AREA)
    a=cv2.Laplacian(ref,cv2.CV_32F)
    b=cv2.Laplacian(actual,cv2.CV_32F)
    dx,dy=cv2.phaseCorrelate(a,b)[0]
    guess=(round(dx),round(dy))
    if any(abs(v)>limit for v in guess):
        shifts=[(x,y) for y in range(-limit,limit+1,5) for x in range(-limit,limit+1,5)]
    else:
        shifts=[(x,y) for y in range(max(-limit,guess[1]-6),min(limit,guess[1]+6)+1)
                      for x in range(max(-limit,guess[0]-6),min(limit,guess[0]+6)+1)]
    def score(p):
        x,y=p; x0=max(0,x);x1=min(w,w+x);y0=max(0,y);y1=min(h,h+y)
        if x0>=x1 or y0>=y1:return float('inf')
        r=ref[y0:y1,x0:x1]; t=actual[y0-y:y1-y,x0-x:x1-x]
        active=(r<225)|(t<225)
        return float(np.minimum(cv2.absdiff(r,t)[active],100).mean()) if active.sum()>250 else float('inf')
    best=min(shifts,key=score)
    if any(abs(v)>limit for v in guess):
        bx,by=best
        best=min(((x,y) for y in range(max(-limit,by-5),min(limit,by+5)+1)
                          for x in range(max(-limit,bx-5),min(limit,bx+5)+1)),key=score)
    x,y=best
    aligned=cv2.warpAffine(actual,np.float32([[1,0,x],[0,1,y]]),(w,h),borderValue=255)
    valid=np.zeros((h,w),dtype=bool)
    valid[max(0,y):min(h,h+y),max(0,x):min(w,w+x)]=True
    return aligned,valid,best,score(best)

def compare(reference, reconstructed, output, width=650):
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    reference=fitz.open(reference);reconstructed=fitz.open(reconstructed)
    pages=[]
    for i in range(min(len(reference),len(reconstructed))):
        a=render(reference,i,width);b=render(reconstructed,i,width)
        aligned,valid,(x,y),error=match(a,b)
        diff=cv2.absdiff(a,aligned)
        heat=cv2.applyColorMap(np.minimum(diff.astype(np.uint16)*3,255).astype(np.uint8),cv2.COLORMAP_TURBO)
        heat[~valid]=(40,40,40)
        cv2.imwrite(str(output/f'page-{i+1:02d}-diff.png'),heat)
        ink=(a<225)&valid
        pages.append({'page':i+1,'dx_px':x,'dy_px':y,
                      'mae':round(float(diff[valid].mean()),3),
                      'ink_mae':round(float(diff[ink].mean()),3) if ink.any() else None,
                      'different_gt30_pct':round(100*float(np.mean(diff[valid]>30)),3),
                      'overlap_pct':round(100*float(valid.mean()),2),
                      'registration_error':round(error,3)})
    result={'reference_pages':len(reference),'reconstructed_pages':len(reconstructed),
            'page_count_matches':len(reference)==len(reconstructed),
            'pages':pages,
            'warning':'Metrics are diagnostic, not proof of pixel-perfect recovery. Margins, zoom, scaling, and rendering alter differences.'}
    (output/'comparison.json').write_text(json.dumps(result,indent=2))
    if pages:
        with (output/'pages.csv').open('w',newline='') as file:
            writer=csv.DictWriter(file,fieldnames=pages[0].keys());writer.writeheader();writer.writerows(pages)
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
