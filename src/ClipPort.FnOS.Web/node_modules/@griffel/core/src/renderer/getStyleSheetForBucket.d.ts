import type { GriffelRenderer, IsomorphicStyleSheet, StyleBucketName } from '../types.js';
/**
 * Ordered style buckets using their short pseudo name.
 *
 * @internal
 */
export declare const styleBucketOrdering: StyleBucketName[];
export declare function getStyleSheetKey(bucketName: StyleBucketName, mediaValue: string, priority: number | string): string;
export declare function getStyleSheetKeyFromElement(styleEl: HTMLStyleElement): string;
/**
 * Lazily adds a `<style>` bucket to the `<head>`. This will ensure that the style buckets are ordered.
 */
export declare function getStyleSheetForBucket(bucketName: StyleBucketName, targetDocument: Document | undefined, insertionPoint: HTMLElement | null, renderer: GriffelRenderer, metadata?: Record<string, unknown>): IsomorphicStyleSheet;
export declare function isSameInsertionKey(elementA: HTMLStyleElement, bucketNameB: StyleBucketName, metadataB: Record<string, unknown>): boolean;
