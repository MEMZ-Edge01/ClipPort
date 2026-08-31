import * as React from 'react';
/**
 * Applies the inline styles shared by both font-icon factories:
 * - maps `primaryFill` -> `color` (unless `currentColor`)
 * - applies an explicit `fontSize`
 *
 * Mutates `state.style` in place to match the existing factory behaviour.
 */
export const applyFontStyle = (state, primaryFill, fontSize) => {
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
/**
 * Renders the `<i>` element body for a font icon. `state` must already carry
 * className/style/a11y; the glyph `codepoint` is the element's text content.
 */
export const renderFontBody = (state, codepoint) => React.createElement('i', state, codepoint);
