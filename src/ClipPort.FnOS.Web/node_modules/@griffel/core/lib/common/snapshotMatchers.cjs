"use strict";
Object.defineProperty(exports, "__esModule", {
    value: true
});
const _vitest = require("vitest");
const _prettier = /*#__PURE__*/ _interop_require_wildcard(require("prettier"));
const _constants = require("../constants.cjs");
const _normalizeCSSBucketEntry = require("../runtime/utils/normalizeCSSBucketEntry.cjs");
function _getRequireWildcardCache(nodeInterop) {
    if (typeof WeakMap !== "function") return null;
    var cacheBabelInterop = new WeakMap();
    var cacheNodeInterop = new WeakMap();
    return (_getRequireWildcardCache = function(nodeInterop) {
        return nodeInterop ? cacheNodeInterop : cacheBabelInterop;
    })(nodeInterop);
}
function _interop_require_wildcard(obj, nodeInterop) {
    if (!nodeInterop && obj && obj.__esModule) {
        return obj;
    }
    if (obj === null || typeof obj !== "object" && typeof obj !== "function") {
        return {
            default: obj
        };
    }
    var cache = _getRequireWildcardCache(nodeInterop);
    if (cache && cache.has(obj)) {
        return cache.get(obj);
    }
    var newObj = {
        __proto__: null
    };
    var hasPropertyDescriptor = Object.defineProperty && Object.getOwnPropertyDescriptor;
    for(var key in obj){
        if (key !== "default" && Object.prototype.hasOwnProperty.call(obj, key)) {
            var desc = hasPropertyDescriptor ? Object.getOwnPropertyDescriptor(obj, key) : null;
            if (desc && (desc.get || desc.set)) {
                Object.defineProperty(newObj, key, desc);
            } else {
                newObj[key] = obj[key];
            }
        }
    }
    newObj.default = obj;
    if (cache) {
        cache.set(obj, newObj);
    }
    return newObj;
}
const { toMatchInlineSnapshot } = _vitest.Snapshots;
// eslint-disable-next-line eqeqeq
const isObject = (value)=>value != null && !Array.isArray(value) && typeof value === 'object';
const isRenderer = (value)=>isObject(value) && isObject(value.stylesheets);
const isStyleRulesTuple = (value)=>Array.isArray(value) && value.length === 2 && !Array.isArray(value[0]);
function rendererToCSS(renderer) {
    const stylesheetKeys = Object.keys(renderer.stylesheets);
    const rules = stylesheetKeys.reduce((acc, styleEl)=>{
        var _a;
        const stylesheet = renderer.stylesheets[styleEl];
        if (stylesheet) {
            const cssRules = (_a = stylesheet.cssRules()) !== null && _a !== void 0 ? _a : [];
            const attributes = Object.entries(stylesheet.elementAttributes).filter(([key])=>key !== _constants.DATA_BUCKET_ATTR);
            if (cssRules.length === 0) {
                return acc;
            }
            return [
                ...acc,
                `/** bucket "${styleEl.slice(0, 1)}"${attributes.length > 0 ? ' ' + JSON.stringify(Object.fromEntries(attributes)) : ''} **/`,
                ...cssRules
            ];
        }
        return acc;
    }, []);
    return rules.join('\n');
}
function styleRulesToCSS(value) {
    const cssRulesByBucket = value[1];
    const keys = Object.keys(cssRulesByBucket);
    return keys.flatMap((styleBucketName)=>cssRulesByBucket[styleBucketName].map((entry)=>(0, _normalizeCSSBucketEntry.normalizeCSSBucketEntry)(entry)[0])).join('\n');
}
function resetStyleRulesToCSS(value) {
    const cssRulesByBucket = Array.isArray(value[2]) ? {
        r: value[2]
    } : value[2];
    return Object.entries(cssRulesByBucket).filter(([, rules])=>rules.length > 0).flatMap(([key, rules])=>[
            `/** bucket "${key}" */`,
            rules.join('\n')
        ]).join('\n');
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
_vitest.expect.extend({
    async toMatchFormattedInlineSnapshot (received, inlineSnapshot) {
        // Capture the call site synchronously so vitest reports the right location on failure.
        // Must set the flag on this.assertion (the Chai assertion object) because toMatchInlineSnapshot
        // reads the error via chai.util.flag(assertion, 'error') where assertion = this.assertion.
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        _vitest.chai.util.flag(this.assertion, 'error', new Error());
        const rawCSS = toRawCSS(received);
        const formatted = (await _prettier.format(rawCSS, {
            parser: 'css'
        })).trim();
        return toMatchInlineSnapshot.call(this, formatted, inlineSnapshot);
    }
}); //# sourceMappingURL=snapshotMatchers.js.map
