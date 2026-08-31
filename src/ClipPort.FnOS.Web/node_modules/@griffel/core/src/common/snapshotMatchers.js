import { expect, chai, Snapshots } from 'vitest';
import * as prettier from 'prettier';
import { DATA_BUCKET_ATTR } from '../constants.js';
import { normalizeCSSBucketEntry } from '../runtime/utils/normalizeCSSBucketEntry.js';
const { toMatchInlineSnapshot } = Snapshots;
// eslint-disable-next-line eqeqeq
const isObject = (value) => value != null && !Array.isArray(value) && typeof value === 'object';
const isRenderer = (value) => isObject(value) && isObject(value.stylesheets);
const isStyleRulesTuple = (value) => Array.isArray(value) && value.length === 2 && !Array.isArray(value[0]);
function rendererToCSS(renderer) {
    const stylesheetKeys = Object.keys(renderer.stylesheets);
    const rules = stylesheetKeys.reduce((acc, styleEl) => {
        var _a;
        const stylesheet = renderer.stylesheets[styleEl];
        if (stylesheet) {
            const cssRules = (_a = stylesheet.cssRules()) !== null && _a !== void 0 ? _a : [];
            const attributes = Object.entries(stylesheet.elementAttributes).filter(([key]) => key !== DATA_BUCKET_ATTR);
            if (cssRules.length === 0) {
                return acc;
            }
            return [
                ...acc,
                `/** bucket "${styleEl.slice(0, 1)}"${attributes.length > 0 ? ' ' + JSON.stringify(Object.fromEntries(attributes)) : ''} **/`,
                ...cssRules,
            ];
        }
        return acc;
    }, []);
    return rules.join('\n');
}
function styleRulesToCSS(value) {
    const cssRulesByBucket = value[1];
    const keys = Object.keys(cssRulesByBucket);
    return keys
        .flatMap(styleBucketName => cssRulesByBucket[styleBucketName].map(entry => normalizeCSSBucketEntry(entry)[0]))
        .join('\n');
}
function resetStyleRulesToCSS(value) {
    const cssRulesByBucket = Array.isArray(value[2]) ? { r: value[2] } : value[2];
    return Object.entries(cssRulesByBucket)
        .filter(([, rules]) => rules.length > 0)
        .flatMap(([key, rules]) => [`/** bucket "${key}" */`, rules.join('\n')])
        .join('\n');
}
function toRawCSS(value) {
    if (typeof value === 'string') {
        return value;
    }
    if (isRenderer(value)) {
        return rendererToCSS(value);
    }
    if (isStyleRulesTuple(value)) {
        return styleRulesToCSS(value);
    }
    if (Array.isArray(value)) {
        return resetStyleRulesToCSS(value);
    }
    throw new Error(`toMatchFormattedInlineSnapshot: unsupported value type "${typeof value}"`);
}
expect.extend({
    async toMatchFormattedInlineSnapshot(received, inlineSnapshot) {
        // Capture the call site synchronously so vitest reports the right location on failure.
        // Must set the flag on this.assertion (the Chai assertion object) because toMatchInlineSnapshot
        // reads the error via chai.util.flag(assertion, 'error') where assertion = this.assertion.
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        chai.util.flag(this.assertion, 'error', new Error());
        const rawCSS = toRawCSS(received);
        const formatted = (await prettier.format(rawCSS, { parser: 'css' })).trim();
        return toMatchInlineSnapshot.call(this, formatted, inlineSnapshot);
    },
});
//# sourceMappingURL=snapshotMatchers.js.map