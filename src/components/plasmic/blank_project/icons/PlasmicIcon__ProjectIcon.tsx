// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type ProjectIconIconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function ProjectIconIcon(props: ProjectIconIconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 40 34"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <path
        d={
          "M2 0h36a2 2 0 012 2v22a2 2 0 01-2 2H23v4h4a2 2 0 012 2v2H11v-2a2 2 0 012-2h4v-4H2a2 2 0 01-2-2V2a2 2 0 012-2zm1 3v20h34V3H3zm2 2h4v4H5V5zm6 0h24v4H11V5zm-6 6h4v4H5v-4zm6 0h24v4H11v-4zm-6 6h4v4H5v-4zm6 0h24v4H11v-4z"
        }
        fill={"currentColor"}
      ></path>
    </svg>
  );
}

export default ProjectIconIcon;
/* prettier-ignore-end */
