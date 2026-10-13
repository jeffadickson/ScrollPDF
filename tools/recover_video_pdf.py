#!/usr/bin/env python3
"""Offline MKV-to-PDF recovery prototype. Requires opencv-python, numpy, Pillow, reportlab."""
import argparse, cv2, numpy as np, json
from pathlib import Path
from PIL import Image
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader

def features(frame):
    g=cv2.cvtColor(frame[:,45:-50],cv2.COLOR_BGR2GRAY)
    return cv2.resize(g,(240,g.shape[0]//3),interpolation=cv2.INTER_AREA)

def align(a,b):
    if np.mean(cv2.absdiff(a,b))<.4:return 0,1.,'stationary'
    x=cv2.Sobel(a,cv2.CV_32F,0,1,ksize=3)
    y=cv2.Sobel(b,cv2.CV_32F,0,1,ksize=3)
    start=max(range(8,210,12),key=lambda i:np.std(y[i:i+52]))
    scores=cv2.matchTemplate(x,y[start:start+52],cv2.TM_CCOEFF_NORMED).ravel()
    scores[:start]=-2
    scores[min(len(scores),start+295):]=-2
    pos=int(np.argmax(scores))
    shift=(pos-start)*3
    quality=float(scores[pos])
    if shift<3:return 0,quality,'stationary' if quality>.85 else 'uncertain'
    return shift,quality,'matched' if quality>=.68 else 'uncertain'

def reconstruct(video,output):
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    inp=cv2.VideoCapture(str(video))
    ok,prev=inp.read()
    if not ok:raise ValueError('No decodable video frames')
    h,w=prev.shape[:2];last=features(prev)
    strips=[prev.copy()];log=[];frame=1
    while True:
        ok,cur=inp.read()
        if not ok:break
        ft=features(cur);offset,score,state=align(last,ft)
        if state=='matched' and 0<offset<h:strips.append(cur[-offset:].copy())
        log.append({'frame':frame,'shift_pixels':offset,'confidence':round(score,4),'status':state})
        last=ft;frame+=1
    inp.release()
    allrows=np.concatenate(strips,axis=0);length=len(allrows)
    # Candidate gray gutters; repeated page spacing disambiguates internal rules.
    row=allrows[:,40:w-45:12,:].mean(axis=(1,2))
    mask=(row>226)&(row<250);runs=[];begin=None
    for y,flag in enumerate(mask):
        if flag and begin is None:begin=y
        if not flag and begin is not None:
            if 4<=y-begin<=50:runs.append((begin,y))
            begin=None
    breaks=[]
    for a,b in runs:
        p=(a+b)//2
        if 400<p<length-75 and (not breaks or p-breaks[-1]>750):breaks.append(p)
    diffs=[breaks[j]-breaks[i] for i in range(len(breaks)) for j in range(i+1,len(breaks))]
    candidate=[d for d in diffs if 1180<=d<=1400]
    pitch=int(np.median(candidate)) if candidate else round((w-45)*841.8/595.2)
    anchor=max(breaks,key=lambda p:sum(abs((q-p)/pitch-round((q-p)/pitch))<.035 for q in breaks)) if breaks else pitch
    boundaries=[]
    for k in range(-30,31):
        expected=anchor+k*pitch
        if not 400<expected<length-70:continue
        near=min(breaks,key=lambda p:abs(p-expected)) if breaks else expected
        boundaries.append(int(near if abs(near-expected)<=28 else expected))
    bounds=[0]+sorted(set(boundaries))+[length]
    pdf=canvas.Canvas(str(output/'reconstructed.pdf'),pagesize=(595.2,841.8))
    for top,bottom in zip(bounds[:-1],bounds[1:]):
        chunk=allrows[top:bottom,35:w-10]
        im=Image.fromarray(cv2.cvtColor(chunk,cv2.COLOR_BGR2RGB))
        scaled=min(841.8,(bottom-top)*595.2/chunk.shape[1])
        pdf.drawImage(ImageReader(im),0,841.8-scaled,width=595.2,height=scaled)
        pdf.showPage()
    pdf.save()
    report={'video_frames':frame,'pages':len(bounds)-1,'page_heights':np.diff(bounds).tolist(),
            'uncertain_frames':[d['frame'] for d in log if d['status']=='uncertain'],
            'alignments':log}
    (output/'diagnostics.json').write_text(json.dumps(report,indent=2))
    return report

if __name__=='__main__':
    a=argparse.ArgumentParser();a.add_argument('video');a.add_argument('--output',default='recovered')
    v=a.parse_args();print(json.dumps({k:val for k,val in reconstruct(v.video,v.output).items() if k!='alignments'},indent=2))
