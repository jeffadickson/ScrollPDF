# Architecture

Capture -> Channel<Bitmap> -> Analyze -> Channel<Bitmap> -> PdfPageWriter.

Bounded channels enforce backpressure. Workers run independently; one worker owns PDFsharp. Capture cancellation permits downstream draining.

## Next milestones

1. Propagate failures across all workers and avoid deadlocks if PDF writer fails.
2. Rolling boundary detection across successive strips.
3. Reject excessive scroll displacement; report capture confidence.
4. Parallel image compression only after profiling, with ordered results.
5. Windows visual smoke tests.