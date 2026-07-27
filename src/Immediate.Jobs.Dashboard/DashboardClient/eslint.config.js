import eslint from '@eslint/js';
import pluginVue from 'eslint-plugin-vue';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
	{
		ignores: ['../Assets/**', 'node_modules/**'],
	},
	eslint.configs.recommended,
	...tseslint.configs.recommended,
	...pluginVue.configs['flat/recommended'],
	{
		files: ['**/*.{ts,vue}'],
		languageOptions: {
			globals: globals.browser,
		},
		rules: {
			'vue/html-indent': ['error', 'tab'],
			'vue/html-self-closing': 'off',
			'vue/max-attributes-per-line': 'off',
			'vue/multi-word-component-names': 'off',
			'vue/singleline-html-element-content-newline': 'off',
		},
	},
	{
		files: ['**/*.vue'],
		languageOptions: {
			parserOptions: {
				parser: tseslint.parser,
			},
		},
	},
	{
		files: ['tests/**/*.ts', 'vite.config.ts', 'scripts/**/*.mjs'],
		languageOptions: {
			globals: {
				...globals.browser,
				...globals.node,
			},
		},
	},
);
