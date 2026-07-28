// @ts-check
import { defineConfig } from 'astro/config';
import mermaid from 'astro-mermaid';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

const repository = 'https://github.com/Lumbridge/UnifyEMPI';
const documentationUrl = 'https://lumbridge.github.io/UnifyEMPI/';

export default defineConfig({
	site: 'https://lumbridge.github.io',
	base: '/UnifyEMPI',
	integrations: [
		mermaid({
			autoTheme: true,
			enableLog: false,
			mermaidConfig: {
				flowchart: { curve: 'basis', htmlLabels: true },
				themeVariables: {
					fontFamily: 'Inter, ui-sans-serif, system-ui, sans-serif',
					primaryColor: '#dff8f3',
					primaryTextColor: '#102a43',
					primaryBorderColor: '#0f766e',
					lineColor: '#0f766e',
					secondaryColor: '#e8eef8',
					tertiaryColor: '#fff8e8',
				},
			},
		}),
		starlight({
			title: 'UnifyEMPI',
			description:
				'Architecture, matching rules, deployment and operational guidance for the UnifyEMPI enterprise master patient index.',
			favicon: '/og.png',
			lastUpdated: true,
			editLink: {
				baseUrl: `${repository}/edit/master/docs-site/`,
			},
			customCss: ['./src/styles/custom.css'],
			head: [
				{
					tag: 'meta',
					attrs: { property: 'og:type', content: 'website' },
				},
				{
					tag: 'meta',
					attrs: { property: 'og:url', content: documentationUrl },
				},
				{
					tag: 'meta',
					attrs: {
						property: 'og:image',
						content: `${documentationUrl}og.png`,
					},
				},
				{
					tag: 'meta',
					attrs: {
						name: 'twitter:card',
						content: 'summary_large_image',
					},
				},
			],
			social: [
				{
					icon: 'github',
					label: 'UnifyEMPI on GitHub',
					href: repository,
				},
			],
			plugins: [
				starlightLinksValidator({
					errorOnInvalidHashes: true,
				}),
			],
			sidebar: [
				{
					label: 'Start here',
					items: [
						{ label: 'Documentation home', slug: 'index' },
						{ label: 'Quick start', slug: 'getting-started' },
						{ label: 'Feature status', slug: 'reference/feature-status' },
					],
				},
				{
					label: 'Concepts',
					items: [
						{ label: 'Identity model and FAQ', slug: 'concepts/identity-model' },
						{ label: 'NHS Wales source model', slug: 'concepts/nhs-wales-sources' },
					],
				},
				{
					label: 'Matching',
					items: [{ label: 'Matching and blocking rules', slug: 'matching/rules' }],
				},
				{
					label: 'Architecture',
					items: [
						{ label: 'System overview', slug: 'architecture/overview' },
						{ label: 'Core processing paths', slug: 'architecture/core-paths' },
						{
							label: 'Decision records',
							items: [
								{
									label: 'ADR 0001: Modular monolith',
									slug: 'architecture/decisions/0001-modular-monolith-and-provider-contract',
								},
							],
						},
					],
				},
				{
					label: 'Operate and integrate',
					items: [
						{ label: 'Configuration reference', slug: 'reference/configuration' },
						{ label: 'Re-index and reconciliation', slug: 'guides/maintenance' },
						{ label: 'Public GCP demo', slug: 'deployment/public-demo' },
						{ label: 'Postman collection', slug: 'guides/postman' },
					],
				},
				{
					label: 'Governance',
					items: [
						{ label: 'Production readiness', slug: 'governance/production-readiness' },
						{ label: 'Security policy', slug: 'governance/security' },
					],
				},
				{
					label: 'Development',
					items: [
						{ label: 'Contributing', slug: 'development/contributing' },
						{ label: 'Performance gates', slug: 'development/performance' },
						{ label: 'Live GCP tests', slug: 'development/live-gcp-tests' },
					],
				},
				{
					label: 'Live resources',
					items: [
						{
							label: 'Demo operations portal',
							link: 'https://unifyempi-demo-mjpwolhr6q-nw.a.run.app',
						},
						{
							label: 'FHIR R4 CapabilityStatement',
							link: 'https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app/fhir/R4/metadata',
						},
					],
				},
			],
		}),
	],
});
