"use strict";
Object.defineProperty(exports, "__esModule", {
    value: true
});
function _export(target, all) {
    for(var name in all)Object.defineProperty(target, name, {
        enumerable: true,
        get: Object.getOwnPropertyDescriptor(all, name).get
    });
}
_export(exports, {
    get getStyleSheetForBucket () {
        return getStyleSheetForBucket;
    },
    get getStyleSheetKey () {
        return getStyleSheetKey;
    },
    get getStyleSheetKeyFromElement () {
        return getStyleSheetKeyFromElement;
    },
    get isSameInsertionKey () {
        return isSameInsertionKey;
    },
    get styleBucketOrdering () {
        return styleBucketOrdering;
    }
});
const _constants = require("../constants.cjs");
const _createIsomorphicStyleSheet = require("./createIsomorphicStyleSheet.cjs");
const styleBucketOrdering = [
    // reset styles
    'r',
    // catch-all
    'd',
    // link
    'l',
    // visited
    'v',
    // focus-within
    'w',
    // focus
    'f',
    // focus-visible
    'i',
    // hover
    'h',
    // active
    'a',
    // at rules for reset styles
    's',
    // keyframes
    'k',
    // at-rules
    't',
    // @media rules
    'm',
    // @container rules (legacy/shared)
    'c',
    // @container rules (sorted)
    'x'
];
// avoid repeatedly calling `indexOf` to determine order during new insertions
const styleBucketOrderingMap = /*#__PURE__*/ styleBucketOrdering.reduce((acc, cur, j)=>{
    acc[cur] = j;
    return acc;
}, {});
function getStyleSheetKey(bucketName, mediaValue, priority) {
    if (bucketName === 'm' || bucketName === 'x') {
        return bucketName + mediaValue + priority;
    }
    return bucketName + priority;
}
function getStyleSheetKeyFromElement(styleEl) {
    var _a;
    const bucketName = styleEl.getAttribute(_constants.DATA_BUCKET_ATTR);
    const priority = (_a = styleEl.getAttribute(_constants.DATA_PRIORITY_ATTR)) !== null && _a !== void 0 ? _a : '0';
    const container = styleEl.getAttribute(_constants.DATA_CONTAINER_ATTR);
    return getStyleSheetKey(bucketName, container !== null && container !== void 0 ? container : styleEl.media || '0', priority);
}
function getStyleSheetForBucket(bucketName, targetDocument, insertionPoint, renderer, metadata = {}) {
    var _a, _b;
    const isMediaBucket = bucketName === 'm';
    const isContainerBucket = bucketName === 'x';
    const media = metadata['m'];
    const container = metadata['x'];
    const priority = (_a = metadata['p']) !== null && _a !== void 0 ? _a : 0;
    const stylesheetKey = getStyleSheetKey(bucketName, (_b = container !== null && container !== void 0 ? container : media) !== null && _b !== void 0 ? _b : '0', priority);
    if (!renderer.stylesheets[stylesheetKey]) {
        const tag = targetDocument && targetDocument.createElement('style');
        const stylesheet = (0, _createIsomorphicStyleSheet.createIsomorphicStyleSheet)(tag, bucketName, priority, {
            ...renderer.styleElementAttributes,
            ...isMediaBucket && {
                media
            },
            ...isContainerBucket && {
                [_constants.DATA_CONTAINER_ATTR]: container
            }
        });
        renderer.stylesheets[stylesheetKey] = stylesheet;
        if ((targetDocument === null || targetDocument === void 0 ? void 0 : targetDocument.head) && tag) {
            targetDocument.head.insertBefore(tag, findInsertionPoint(targetDocument, insertionPoint, bucketName, renderer, metadata));
        }
    }
    return renderer.stylesheets[stylesheetKey];
}
function isSameInsertionKey(elementA, bucketNameB, metadataB) {
    // The bucket name is the key; only "@media" / "@container" buckets append a condition, each from the
    // field that matches its bucket (mirroring "getStyleSheetKey").
    const elementBucketA = elementA.getAttribute(_constants.DATA_BUCKET_ATTR);
    const isSameBucket = elementBucketA === bucketNameB;
    if (isSameBucket) {
        if (bucketNameB === 'm') {
            return metadataB['m'] === elementA.media;
        }
        if (bucketNameB === 'x') {
            return metadataB['x'] === elementA.getAttribute(_constants.DATA_CONTAINER_ATTR);
        }
        return true;
    }
    return false;
}
/**
 * Finds an element before which the new bucket style element should be inserted following the bucket sort order.
 *
 * @param targetDocument - A document
 * @param insertionPoint - An element that will be used as an initial insertion point
 * @param targetBucket - The bucket that should be inserted to DOM
 * @param renderer - Griffel renderer
 * @param metadata - metadata for CSS rule
 * @returns - Smallest style element with greater sort order than the current bucket
 */ function findInsertionPoint(targetDocument, insertionPoint, targetBucket, renderer, metadata = {}) {
    var _a, _b, _c;
    const targetOrder = styleBucketOrderingMap[targetBucket];
    const media = (_a = metadata['m']) !== null && _a !== void 0 ? _a : '';
    const container = (_b = metadata['x']) !== null && _b !== void 0 ? _b : '';
    const priority = (_c = metadata['p']) !== null && _c !== void 0 ? _c : 0;
    // Similar to javascript sort comparators where
    // a positive value is increasing sort order
    // a negative value is decreasing sort order
    let comparer = (el)=>targetOrder - styleBucketOrderingMap[el.getAttribute(_constants.DATA_BUCKET_ATTR)];
    let styleElements = targetDocument.head.querySelectorAll(`[${_constants.DATA_BUCKET_ATTR}]`);
    // "@media" and "@container" rules are split into per-condition sheets that must be ordered by
    // their condition (ascending min-width) rather than plain insertion order.
    if (targetBucket === 'm' || targetBucket === 'x') {
        const conditionElements = targetDocument.head.querySelectorAll(`[${_constants.DATA_BUCKET_ATTR}="${targetBucket}"]`);
        // only reduce the scope of the search and change comparer
        // if there are other buckets of the same kind already on the page
        if (conditionElements.length) {
            styleElements = conditionElements;
            comparer = targetBucket === 'm' ? (el)=>renderer.compareMediaQueries(media, el.media) : (el)=>{
                var _a;
                return renderer.compareContainerQueries(container, (_a = el.getAttribute(_constants.DATA_CONTAINER_ATTR)) !== null && _a !== void 0 ? _a : '');
            };
        }
    }
    const comparerWithPriority = (el)=>{
        if (isSameInsertionKey(el, targetBucket, metadata)) {
            return priority - Number(el.getAttribute(_constants.DATA_PRIORITY_ATTR));
        }
        return comparer(el);
    };
    const length = styleElements.length;
    let index = length - 1;
    while(index >= 0){
        const styleElement = styleElements.item(index);
        if (comparerWithPriority(styleElement) > 0) {
            return styleElement.nextSibling;
        }
        index--;
    }
    if (length > 0) {
        return styleElements.item(0);
    }
    return insertionPoint ? insertionPoint.nextSibling : null;
} //# sourceMappingURL=getStyleSheetForBucket.js.map
