function isShaOnly(version) {
    const tags = version.metadata?.container?.tags;
    return Array.isArray(tags) && tags.length === 1 && /^sha-[0-9a-f]{40}$/.test(tags[0]);
}

module.exports = async function cleanup({ github, context, core }) {
    const { owner, repo } = context.repo;
    const { data: account } = await github.rest.users.getByUsername({ username: owner });
    let route;
    let ownerParameters;
    if (account.type === 'Organization') {
        route = '/orgs/{org}/packages/container/{package_name}/versions';
        ownerParameters = { org: owner };
    } else if (account.type === 'User') {
        route = '/users/{username}/packages/container/{package_name}/versions';
        ownerParameters = { username: owner };
    } else {
        throw new Error(`Unsupported package owner type: ${account.type}`);
    }

    const parameters = { ...ownerParameters, package_name: repo.toLowerCase() };
    const versions = await github.paginate(`GET ${route}`, { ...parameters, per_page: 100 });
    let deleted = 0;
    for (const version of versions.filter(isShaOnly)) {
        const versionParameters = { ...parameters, package_version_id: version.id };
        const { data: current } = await github.request(`GET ${route}/{package_version_id}`, versionParameters);
        if (!isShaOnly(current)) {
            core.info(`Preserving version ${version.id}: its tags changed.`);
            continue;
        }

        await github.request(`DELETE ${route}/{package_version_id}`, versionParameters);
        core.info(`Deleted version ${version.id} (${current.metadata.container.tags[0]}).`);
        deleted++;
    }

    await core.summary
        .addHeading('GHCR cleanup')
        .addRaw(`Deleted ${deleted} SHA-only version(s) from ghcr.io/${owner.toLowerCase()}/${parameters.package_name}.`)
        .write();
};
