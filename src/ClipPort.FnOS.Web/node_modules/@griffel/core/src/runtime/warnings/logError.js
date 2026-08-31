export function logError(...args) {
    if (process.env.NODE_ENV !== 'production' && typeof document !== 'undefined') {
        // eslint-disable-next-line no-console
        console.error(...args);
    }
}
//# sourceMappingURL=logError.js.map