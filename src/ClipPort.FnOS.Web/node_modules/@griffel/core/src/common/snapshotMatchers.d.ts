interface CustomMatchers<R = unknown> {
    toMatchFormattedInlineSnapshot(inlineSnapshot?: string): R;
}
declare module 'vitest' {
    interface Assertion<T = any> extends CustomMatchers<T> {
    }
    interface AsymmetricMatchersContaining extends CustomMatchers {
    }
}
export {};
