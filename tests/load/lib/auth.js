import http from 'k6/http';
import { check } from 'k6';

export function loginAs(baseUrl, role) {
  const loginPage = http.get(`${baseUrl}/demo-login`, { tags: { area: 'auth' } });
  check(loginPage, {
    'login page is reachable': (response) => response.status === 200,
  });

  const csrf = extract(loginPage.body, /name="__RequestVerificationToken" type="hidden" value="([^"]+)"/);
  const rolePattern = new RegExp(`<h2 class="h5">${escapeRegExp(role)}</h2>[\\s\\S]*?name="roleToken" value="([^"]+)"`);
  const roleToken = extract(loginPage.body, rolePattern);

  const response = http.post(
    `${baseUrl}/demo-login`,
    {
      roleToken,
      __RequestVerificationToken: csrf,
    },
    {
      redirects: 0,
      tags: { area: 'auth' },
    });

  check(response, {
    'login redirects to dashboard': (item) => item.status === 302,
  });

  const authCookie = response.cookies['.AspNetCore.Identity.Application'];
  if (!authCookie || !authCookie[0] || !authCookie[0].value) {
    const setCookie = response.headers['Set-Cookie'] || '';
    const cookieMatch = /\.AspNetCore\.Identity\.Application=([^;]+)/.exec(setCookie);
    if (!cookieMatch || !cookieMatch[1]) {
      throw new Error('Authentication cookie was not issued.');
    }

    return `.AspNetCore.Identity.Application=${cookieMatch[1]}`;
  }

  return `.AspNetCore.Identity.Application=${authCookie[0].value}`;
}

export function verifyAllRoleLogins(baseUrl) {
  let administratorCookie = '';
  for (const role of [
    'System Administrator',
    'Branch Manager',
    'Sales Representative',
    'Field Technician',
  ]) {
    const cookie = loginAs(baseUrl, role);
    if (role === 'System Administrator') {
      administratorCookie = cookie;
    }
  }

  return administratorCookie;
}

function extract(value, pattern) {
  const match = pattern.exec(value);
  if (!match || !match[1]) {
    throw new Error('Required login token was not present.');
  }

  return match[1];
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
