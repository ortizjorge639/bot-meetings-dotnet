import type { LiveCaptionPayload } from "./contracts";
import { HttpError } from "./api";

export interface DeliveryCounts {
  accepted: number;
  pending: number;
  failed: number;
}

export class CaptionDelivery {
  private queue: { payload: LiveCaptionPayload; enqueuedAt: number }[] = [];
  private controller = new AbortController();
  private running = false;
  private accepted = 0;
  private failed = 0;
  private stopped = false;

  constructor(
    private readonly send: (payload: LiveCaptionPayload, signal: AbortSignal) => Promise<void>,
    private readonly update: (counts: DeliveryCounts) => void,
    private readonly error: (error: unknown) => void
  ) {}

  enqueue(payload: LiveCaptionPayload): void {
    if (this.stopped || this.queue.length >= 100) {
      this.failed++;
      this.error(new Error("Caption queue is stopped or full. A final caption was not delivered."));
      this.publish();
      return;
    }
    this.queue.push({ payload, enqueuedAt: Date.now() });
    this.publish();
    void this.drain();
  }

  stop(): void {
    if (this.stopped) return;
    this.stopped = true;
    this.controller.abort();
    this.failed += this.queue.length;
    this.queue = [];
    this.publish();
  }

  get pending(): number { return this.queue.length; }

  private publish(): void {
    this.update({ accepted: this.accepted, pending: this.queue.length, failed: this.failed });
  }

  private async drain(): Promise<void> {
    if (this.running || this.stopped) return;
    this.running = true;
    try {
      while (this.queue.length && !this.stopped) {
        const item = this.queue[0];
        let terminal = false;
        try {
          for (let attempt = 0; ; attempt++) {
            const remaining = 60_000 - (Date.now() - item.enqueuedAt);
            if (remaining <= 0) throw new Error("Caption delivery exceeded its 60-second age limit.");
            try {
              await this.send(item.payload, AbortSignal.any([
                this.controller.signal, AbortSignal.timeout(Math.min(10_000, remaining))
              ]));
              break;
            } catch (error) {
              if (this.stopped) return;
              const retryable = error instanceof HttpError
                ? error.status === 408 || error.status === 429 || error.status >= 500
                : error instanceof TypeError || (error instanceof DOMException &&
                  (error.name === "TimeoutError" || error.name === "AbortError"));
              terminal = error instanceof HttpError && !retryable;
              if (!retryable || attempt >= 3) throw error;
              const delay = Math.max(1000 * 2 ** attempt,
                error instanceof HttpError ? error.retryAfterMs : 0);
              if (Date.now() - item.enqueuedAt + delay >= 60_000) {
                throw new Error("Caption retry exceeds its 60-second age limit.");
              }
              await wait(delay, this.controller.signal);
            }
          }
          if (this.stopped) return;
          this.accepted++;
        } catch (error) {
          if (this.stopped) return;
          this.failed++;
          this.error(error);
          if (terminal) {
            this.queue.shift();
            this.stop();
            return;
          }
        }
        this.queue.shift();
        this.publish();
      }
    } finally {
      this.running = false;
    }
  }
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    signal.throwIfAborted();
    const abort = (): void => {
      clearTimeout(timer);
      reject(signal.reason);
    };
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", abort);
      resolve();
    }, ms);
    signal.addEventListener("abort", abort, { once: true });
  });
}
