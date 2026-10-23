const assert = require('node:assert/strict');
const { test } = require('node:test');
const cleanup = require('./cleanup-packages.cjs');

const shaTag = `sha-${'a'.repeat(40)}`;
const version = (id, tags) => ({ id, metadata: { container: { tags } } });

function fixture(type = 'User', versions = [], currentVersions = versions) {
    const requests = [];
    const summaries = [];
    const summary = {
        addHeading() { return this; },
        addRaw(text) { summaries.push(text); return this; },
        async write() {}
    };
    return {
        requests,
        summaries,
        arguments: {
            context: { repo: { owner: 'Owner', repo: 'OneDrive-S3-Proxy' } },
            core: { info() {}, summary },
            github: {
                rest: { users: { async getByUsername({ username }) {
                    assert.equal(username, 'Owner');
                    return { data: { type } };
                } } },
                async paginate(route, parameters) {
                    requests.push({ route, parameters });
                    assert.equal(parameters.per_page, 100);
                    return versions;
                },
                async request(route, parameters) {
                    requests.push({ route, parameters });
                    if (route.startsWith('GET ')) {
                        return { data: currentVersions.find(item => item.id === parameters.package_version_id) };
                    }
                    return { status: 204 };
                }
            }
        }
    };
}

test('deletes only versions with exactly one full SHA tag', async () => {
    const data = fixture('User', [
        version(1, [shaTag]),
        version(2, [shaTag, 'latest', '0.1.1']),
        version(3, [shaTag, '0.1.0']),
        version(4, []),
        version(5, ['latest']),
        version(6, ['sha-abc123']),
        version(7, [shaTag, `sha-${'b'.repeat(40)}`]),
        { id: 8 },
        version(9, ['0.1.0']),
        version(10, [`sha-${'x'.repeat(40)}`])
    ]);

    await cleanup(data.arguments);

    const deletions = data.requests.filter(item => item.route.startsWith('DELETE '));
    assert.equal(deletions.length, 1);
    assert.equal(deletions[0].parameters.package_version_id, 1);
    assert.equal(deletions[0].route, 'DELETE /users/{username}/packages/container/{package_name}/versions/{package_version_id}');
    assert.equal(deletions[0].parameters.username, 'Owner');
    assert.equal(deletions[0].parameters.package_name, 'onedrive-s3-proxy');
    assert.match(data.summaries[0], /Deleted 1 SHA-only/);
});

test('uses organization package endpoints for organization owners', async () => {
    const data = fixture('Organization', [version(1, [shaTag])]);
    await cleanup(data.arguments);
    assert.ok(data.requests.every(item => item.route.includes('/orgs/{org}/')));
    assert.ok(data.requests.every(item => item.parameters.org === 'Owner'));
});

test('preserves a candidate that gained a release tag since listing', async () => {
    const data = fixture('User', [version(1, [shaTag])], [version(1, [shaTag, '0.1.1'])]);
    await cleanup(data.arguments);
    assert.equal(data.requests.filter(item => item.route.startsWith('DELETE ')).length, 0);
    assert.match(data.summaries[0], /Deleted 0 SHA-only/);
});

test('handles packages with no versions without attempting deletion', async () => {
    const data = fixture();
    await cleanup(data.arguments);
    assert.equal(data.requests.length, 1);
    assert.match(data.summaries[0], /Deleted 0 SHA-only/);
});

test('surfaces listing and deletion permission errors', async () => {
    for (const operation of ['paginate', 'request']) {
        const data = fixture('User', [version(1, [shaTag])]);
        const original = data.arguments.github[operation];
        data.arguments.github[operation] = async (...args) => {
            if (operation === 'paginate' || args[0].startsWith('DELETE ')) {
                throw new Error('403 Forbidden');
            }
            return original(...args);
        };
        await assert.rejects(cleanup(data.arguments), /403 Forbidden/);
        assert.equal(data.summaries.length, 0);
    }
});
