import { fnosSdk } from './fnosSdk';

const status = document.querySelector<HTMLElement>('#callback-status');
const result = fnosSdk.parseCallback(window.location.href);
const expectedState = sessionStorage.getItem('clipport-auth-state');
const stateIsValid = Boolean(expectedState && result.state === expectedState);

if (window.opener && !window.opener.closed && stateIsValid) {
  window.opener.postMessage({ type: 'clipport:auth-result', result }, window.location.origin);
  if (status) status.textContent = '授权结果已返回 ClipPort，可关闭此窗口。';
  window.close();
} else if (status) {
  status.textContent = stateIsValid
    ? '授权已完成，请返回 ClipPort 并刷新授权目录。'
    : '授权回调校验失败，请返回 ClipPort 重新授权。';
}
