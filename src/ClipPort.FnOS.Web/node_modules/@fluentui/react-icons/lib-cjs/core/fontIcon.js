"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.renderFontBody = exports.applyFontStyle = void 0;
const tslib_1 = require("tslib");
const React = tslib_1.__importStar(require("react"));
/**
 * Applies the inline styles shared by both font-icon factories:
 * - maps `primaryFill` -> `color` (unless `currentColor`)
 * - applies an explicit `fontSize`
 *
 * Mutates `state.style` in place to match the existing factory behaviour.
 */
const applyFontStyle = (state, primaryFill, fontSize) => {
    if (primaryFill && primaryFill.toLowerCase() !== 'currentcolor') {
        state.style = {
            ...state.style,
            color: primaryFill,
        };
    }
    if (fontSize) {
        state.style = {
            ...state.style,
            fontSize,
        };
    }
};
exports.applyFontStyle = applyFontStyle;
/**
 * Renders the `<i>` element body for a font icon. `state` must already carry
 * className/style/a11y; the glyph `codepoint` is the element's text content.
 */
const renderFontBody = (state, codepoint) => React.createElement('i', state, codepoint);
exports.renderFontBody = renderFontBody;
