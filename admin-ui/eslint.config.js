import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    rules: {
      // The admin UI consumes loosely-typed backend JSON payloads throughout;
      // enforcing this as a hard error would require typing ~270 pre-existing
      // call sites unrelated to this change. Kept as a visible warning instead
      // of disabled outright so new/existing `any` usage isn't hidden.
      '@typescript-eslint/no-explicit-any': 'warn',
      // Allows the common `const { omitted, ...rest } = data` destructure-to-
      // exclude-a-field pattern without having to reference `omitted`.
      '@typescript-eslint/no-unused-vars': ['error', { ignoreRestSiblings: true }],
    },
  },
])
